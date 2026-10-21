using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PRImager.AIAssistant.Services;

/// <summary>Qué es un archivo .gguf, desde el punto de vista de quien lo quiere cargar.</summary>
public enum GgufKind
{
    /// <summary>Un modelo de lenguaje completo: se carga solo.</summary>
    Model,
    /// <summary>Proyector multimodal (mmproj): se usa junto a un modelo para que vea imágenes.</summary>
    Projector,
    /// <summary>Cabeza de borrador para decodificación especulativa: capas sueltas, no un modelo.</summary>
    DraftHead,
    /// <summary>Adaptador LoRA: se aplica sobre un modelo base.</summary>
    Adapter,
    /// <summary>Solo el vocabulario, sin pesos.</summary>
    VocabOnly,
    /// <summary>Una parte de un modelo partido en varios archivos.</summary>
    SplitPart
}

/// <summary>Veredicto sobre un .gguf: qué es y, si no es un modelo, por qué, en palabras del usuario.</summary>
public sealed class GgufVerdict
{
    public GgufKind Kind { get; init; }
    public string Reason { get; init; } = "";

    public bool IsModel => Kind == GgufKind.Model;

    public static readonly GgufVerdict PresumedModel = new() { Kind = GgufKind.Model };
}

/// <summary>Lo que importa de la cabecera de un GGUF. Los campos ausentes quedan en null.</summary>
public sealed class GgufHeader
{
    public uint Version { get; init; }
    public ulong TensorCount { get; init; }
    public string? Architecture { get; set; }
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string? SizeLabel { get; set; }
    public ulong? BlockCount { get; set; }
    public ulong? SplitCount { get; set; }
    public ulong? SplitNo { get; set; }

    /// <summary>
    /// Los valores numéricos de la metadata —escalares y arreglos chicos—, por clave
    /// completa. Un escalar es un arreglo de uno; un booleano, 0 o 1. Son los que hacen
    /// falta para calcular la memoria: cabezas, largos de clave, ventana deslizante.
    /// </summary>
    public Dictionary<string, long[]> Numbers { get; } = new(StringComparer.Ordinal);

    /// <summary>True si se llegó a leer toda la metadata, no solo el comienzo.</summary>
    public bool MetadataComplete { get; set; }

    /// <summary>La tabla de tensores, si el bloque leído llegó hasta ella. Si no, null.</summary>
    public GgufTensorSummary? Tensors { get; set; }

    /// <summary>Un valor de la sección de la arquitectura: <c>Arch("block_count")</c> lee <c>llama.block_count</c>.</summary>
    public long[]? Arch(string suffix) =>
        Architecture != null && Numbers.TryGetValue($"{Architecture}.{suffix}", out var v) ? v : null;

    public long? ArchScalar(string suffix) => Arch(suffix) is { Length: > 0 } v ? v[0] : null;
}

/// <summary>
/// Cuánto pesa cada parte del modelo, de la tabla de tensores. Hace falta porque no todo el
/// archivo va a la placa: llama.cpp deja la tabla de embeddings de entrada en la RAM.
/// Medido el 20 de octubre de 2026 en los tres modelos de la máquina de desarrollo: la parte
/// que queda en CPU coincide al megabyte con <c>token_embd</c> más
/// <c>per_layer_token_embd</c> (Gemma 4 E4B: 2.208 MiB de 4.731).
/// </summary>
public sealed class GgufTensorSummary
{
    public long TotalBytes { get; init; }

    /// <summary>Lo que llama.cpp deja en la RAM aunque el modelo vaya entero a la placa.</summary>
    public long CpuResidentBytes { get; init; }

    public long TokenEmbeddingBytes { get; init; }

    /// <summary>
    /// Sin <c>output.weight</c>, la salida reusa los embeddings de entrada y llama.cpp sube
    /// una copia a la placa: por eso Gemma 4 E2B ocupa 216 MiB más de lo que da la resta.
    /// </summary>
    public bool HasOutputTensor { get; init; }

    /// <summary>Lo que va a la placa con todas las capas en GPU.</summary>
    public long GpuBytes => TotalBytes - CpuResidentBytes + (HasOutputTensor ? 0 : TokenEmbeddingBytes);
}

/// <summary>
/// Distingue los .gguf que son modelos de los que no (pendiente 6.5 del maestro).
///
/// HuggingFace publica en un mismo repositorio, al lado del modelo, archivos que no se
/// cargan solos: proyectores, cabezas de borrador, adaptadores, partes de un modelo
/// partido. El caso que lo destapó (maestro 4.22) fue una cabeza de borrador de 3 GB que
/// el catálogo ofreció como un modelo más, y que al cargarla falló con
/// <c>missing tensor 'blk.0.attn_norm.weight'</c>.
///
/// Dos fuentes, en este orden de confianza:
///
///   1. <b>La cabecera</b>. Todo lo necesario está en el primer kilobyte o dos: cantidad de
///      tensores, <c>general.architecture</c>, <c>general.type</c> y
///      <c>{arquitectura}.block_count</c>. La cabeza de borrador de 4.22 declara 65 capas y
///      trae 18 tensores; el modelo completo del mismo repo, 866.
///   2. <b>El nombre</b>, cuando no se tiene la cabecera: en el listado del catálogo, que
///      no baja nada por cada archivo.
///
/// No es código de red: se compila también en la configuración Forense, donde lo usa el
/// selector de modelos.
/// </summary>
public static class GgufInspector
{
    /// <summary>Bytes que alcanzan para leer lo que importa, con margen.</summary>
    public const int ProbeBytes = 64 * 1024;

    // Un bloque de transformer tiene como mínimo norma, Q, K, V, salida y las del FFN;
    // un modelo real anda entre 7 y 20 tensores por capa. Menos de dos por capa no es
    // un modelo completo: son capas sueltas.
    private const int MinTensorsPerBlock = 2;

    private static readonly Regex SplitPattern =
        new(@"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DraftPattern =
        new(@"(?<=^|[-_.])(draft|mtp)(?=$|[-_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LoraPattern =
        new(@"(?<=^|[-_.])lora(?=$|[-_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex VocabPattern =
        new(@"(^|[-_.])vocab(?=$|[-_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Veredicto por el nombre del archivo. Lo que no se reconoce se presume modelo.</summary>
    public static GgufVerdict FromFileName(string fileName)
    {
        string name = Path.GetFileName(fileName);

        var split = SplitPattern.Match(name);
        if (split.Success && split.Groups[2].Value != "00001")
            return SplitVerdict(ulong.Parse(split.Groups[1].Value), ulong.Parse(split.Groups[2].Value));

        if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            return ProjectorVerdict();

        string stem = Path.GetFileNameWithoutExtension(name);
        if (DraftPattern.IsMatch(stem)) return DraftVerdict(null);
        if (LoraPattern.IsMatch(stem)) return AdapterVerdict();
        if (VocabPattern.IsMatch(stem)) return VocabVerdict();

        return GgufVerdict.PresumedModel;
    }

    /// <summary>Veredicto por la cabecera, que es lo que el archivo dice de sí mismo.</summary>
    public static GgufVerdict FromHeader(GgufHeader h)
    {
        if (h.SplitCount is > 1)
            return SplitVerdict((h.SplitNo ?? 0) + 1, h.SplitCount.Value);

        if (string.Equals(h.Architecture, "clip", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h.Type, "mmproj", StringComparison.OrdinalIgnoreCase))
            return ProjectorVerdict();

        if (string.Equals(h.Type, "adapter", StringComparison.OrdinalIgnoreCase))
            return AdapterVerdict();

        if (h.TensorCount == 0)
            return VocabVerdict();

        if (h.BlockCount is > 0 && h.TensorCount < h.BlockCount.Value * MinTensorsPerBlock)
            return DraftVerdict(h);

        return GgufVerdict.PresumedModel;
    }

    /// <summary>
    /// Lee la cabecera de un archivo local. Null si no es un GGUF legible: en ese caso no
    /// hay veredicto, y quien llama decide con lo que tenga.
    /// </summary>
    public static GgufHeader? TryReadFile(string path) => TryReadFile(path, ProbeBytes);

    /// <summary>
    /// Lee la metadata completa y la tabla de tensores de un archivo local. El vocabulario
    /// va antes de la tabla y en un modelo con 262 mil tokens ocupa ~16 MB, así que se lee
    /// lo que haga falta, hasta <see cref="FullReadLimit"/>.
    /// </summary>
    public static GgufHeader? TryReadFileFull(string path) => TryReadFile(path, FullReadLimit);

    /// <summary>Tope de lectura para llegar a la tabla de tensores. Medido: 15,8 MB en Gemma 4.</summary>
    public const int FullReadLimit = 48 * 1024 * 1024;

    private static GgufHeader? TryReadFile(string path, int limit)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(limit, fs.Length)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            return TryParse(buffer.AsSpan(0, read), fs.Length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parsea el comienzo de un GGUF. Tolera que el bloque termine a mitad de la metadata:
    /// devuelve lo que alcanzó a leer, que es lo de arriba y alcanza para el veredicto.
    /// Si el bloque llega hasta la tabla de tensores y se conoce el largo del archivo,
    /// la resume también. Null si ni siquiera es un GGUF.
    /// </summary>
    public static GgufHeader? TryParse(ReadOnlySpan<byte> data, long fileLength = 0)
    {
        if (data.Length < 24 || data[0] != 'G' || data[1] != 'G' || data[2] != 'U' || data[3] != 'F')
            return null;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
        // v1 usaba enteros de 32 bits para los conteos; no se ve en HuggingFace desde 2023
        if (version < 2) return null;

        var header = new GgufHeader
        {
            Version = version,
            TensorCount = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8))
        };
        ulong kvCount = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16));
        ulong alignment = 32;

        var r = new Reader(data, 24);
        ulong done = 0;
        for (; done < kvCount; done++)
        {
            if (!r.TryString(out string key) || !r.TryU32(out uint type)) break;

            if (type == StringType)
            {
                if (!r.TryString(out string s)) break;
                switch (key)
                {
                    case "general.architecture": header.Architecture = s; break;
                    case "general.type": header.Type = s; break;
                    case "general.name": header.Name = s; break;
                    case "general.size_label": header.SizeLabel = s; break;
                }
                continue;
            }

            // El vocabulario es lo único grande y no se necesita: se saltea sin guardarlo
            if (key.StartsWith("tokenizer.", StringComparison.Ordinal))
            {
                if (!r.TrySkip(type)) break;
                continue;
            }

            if (!r.TryNumbers(type, out long[]? values)) break;
            if (values == null) continue; // arreglo de texto o demasiado largo: no interesa

            header.Numbers[key] = values;
            if (key == "split.count") header.SplitCount = (ulong)values[0];
            else if (key == "split.no") header.SplitNo = (ulong)values[0];
            else if (key == "general.alignment" && values[0] > 0) alignment = (ulong)values[0];
        }

        // block_count lleva la arquitectura adelante: llama.block_count, qwen35.block_count.
        // Se toma el de la arquitectura, no el de un codificador de visión que venga adentro.
        header.BlockCount = header.ArchScalar("block_count") is { } b ? (ulong)b
            : header.Numbers.FirstOrDefault(kv => kv.Key.EndsWith(".block_count", StringComparison.Ordinal)
                                                  && !kv.Key.StartsWith("clip.", StringComparison.Ordinal)).Value
                is { Length: > 0 } any ? (ulong)any[0] : null;

        header.MetadataComplete = done == kvCount;
        if (header.MetadataComplete && fileLength > 0)
            header.Tensors = ReadTensorTable(ref r, header.TensorCount, alignment, fileLength);

        return header;
    }

    /// <summary>
    /// Resume la tabla de tensores. El tamaño de cada uno sale de la distancia entre
    /// offsets, sin necesitar la tabla de tipos de ggml, que cambia con cada cuantización
    /// nueva. Null si el bloque se corta antes del final de la tabla.
    /// </summary>
    private static GgufTensorSummary? ReadTensorTable(ref Reader r, ulong count, ulong alignment, long fileLength)
    {
        if (count == 0 || count > 1_000_000) return null;

        var entries = new List<(ulong offset, string name)>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            if (!r.TryString(out string name) || !r.TryU32(out uint dims)) return null;
            for (uint d = 0; d < dims; d++)
                if (!r.TrySkip(10)) return null;            // cada dimensión es un u64
            if (!r.TryU32(out _) || !r.TryU64Public(out ulong offset)) return null;
            entries.Add((offset, name));
        }

        ulong dataStart = ((ulong)r.Position + alignment - 1) / alignment * alignment;
        long dataLength = fileLength - (long)dataStart;
        if (dataLength <= 0) return null;

        entries.Sort((a, b) => a.offset.CompareTo(b.offset));
        long total = 0, cpu = 0, embd = 0;
        bool hasOutput = false;
        for (int i = 0; i < entries.Count; i++)
        {
            long next = i + 1 < entries.Count ? (long)entries[i + 1].offset : dataLength;
            long size = next - (long)entries[i].offset;
            if (size < 0) return null;
            total += size;

            string n = entries[i].name;
            if (n == "token_embd.weight") { embd = size; cpu += size; }
            else if (n == "per_layer_token_embd.weight") cpu += size;
            else if (n == "output.weight") hasOutput = true;
        }

        return new GgufTensorSummary
        {
            TotalBytes = total,
            CpuResidentBytes = cpu,
            TokenEmbeddingBytes = embd,
            HasOutputTensor = hasOutput
        };
    }

    // --- veredictos: el texto lo lee el usuario ---

    private static GgufVerdict SplitVerdict(ulong part, ulong total) => new()
    {
        Kind = GgufKind.SplitPart,
        Reason = Loc.F("gguf.split", part, total)
    };

    private static GgufVerdict ProjectorVerdict() => new()
    {
        Kind = GgufKind.Projector,
        Reason = Loc.T("gguf.projector")
    };

    private static GgufVerdict DraftVerdict(GgufHeader? h) => new()
    {
        Kind = GgufKind.DraftHead,
        Reason = Loc.T("gguf.draft") +
                 (h?.BlockCount is { } b
                     ? " " + Loc.F("gguf.draft.detail", b, h.TensorCount)
                     : "")
    };

    private static GgufVerdict AdapterVerdict() => new()
    {
        Kind = GgufKind.Adapter,
        Reason = Loc.T("gguf.adapter")
    };

    private static GgufVerdict VocabVerdict() => new()
    {
        Kind = GgufKind.VocabOnly,
        Reason = Loc.T("gguf.vocab")
    };

    // --- lectura del formato ---

    private const uint StringType = 8;
    private const uint ArrayType = 9;

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data, int pos)
        {
            _data = data;
            _pos = pos;
        }

        private bool Has(ulong n) => (ulong)(_data.Length - _pos) >= n;

        public int Position => _pos;

        public bool TryU64Public(out ulong value) => TryU64(out value);

        /// <summary>
        /// Lee un valor numérico, escalar o arreglo, como enteros. Los flotantes se truncan:
        /// acá solo interesan conteos y largos. Un arreglo de texto o de más de
        /// <see cref="MaxArray"/> elementos se saltea y devuelve null con true.
        /// </summary>
        public bool TryNumbers(uint type, out long[]? values)
        {
            values = null;
            if (type == ArrayType)
            {
                int start = _pos;
                if (!TryU32(out uint itemType) || !TryU64(out ulong count)) return false;
                if (itemType == StringType || itemType == ArrayType || count > MaxArray)
                {
                    _pos = start;
                    return TrySkip(type);
                }
                var list = new long[count];
                for (ulong i = 0; i < count; i++)
                    if (!TryScalar(itemType, out list[i])) return false;
                values = list;
                return true;
            }

            if (!TryScalar(type, out long v)) return false;
            values = new[] { v };
            return true;
        }

        private bool TryScalar(uint type, out long value)
        {
            value = 0;
            int size = ScalarSize(type);
            if (size == 0 || !Has((ulong)size)) return false;
            var s = _data.Slice(_pos, size);
            value = type switch
            {
                0 => s[0],
                1 => (sbyte)s[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                3 => BinaryPrimitives.ReadInt16LittleEndian(s),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                5 => BinaryPrimitives.ReadInt32LittleEndian(s),
                6 => (long)BinaryPrimitives.ReadSingleLittleEndian(s),
                7 => s[0] != 0 ? 1 : 0,
                10 => (long)BinaryPrimitives.ReadUInt64LittleEndian(s),
                11 => BinaryPrimitives.ReadInt64LittleEndian(s),
                12 => (long)BinaryPrimitives.ReadDoubleLittleEndian(s),
                _ => 0
            };
            _pos += size;
            return true;
        }

        private const ulong MaxArray = 4096;

        public bool TryU32(out uint value)
        {
            value = 0;
            if (!Has(4)) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos));
            _pos += 4;
            return true;
        }

        private bool TryU64(out ulong value)
        {
            value = 0;
            if (!Has(8)) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos));
            _pos += 8;
            return true;
        }

        public bool TryString(out string value)
        {
            value = "";
            if (!TryU64(out ulong len) || !Has(len)) return false;
            value = Encoding.UTF8.GetString(_data.Slice(_pos, (int)len));
            _pos += (int)len;
            return true;
        }

        public bool TrySkip(uint type)
        {
            if (type == StringType) return TryString(out _);

            if (type == ArrayType)
            {
                if (!TryU32(out uint itemType) || !TryU64(out ulong count)) return false;
                if (itemType == StringType)
                {
                    for (ulong i = 0; i < count; i++)
                        if (!TryString(out _)) return false;
                    return true;
                }
                int itemSize = ScalarSize(itemType);
                if (itemSize == 0 || count > (ulong)int.MaxValue) return false;
                ulong bytes = count * (ulong)itemSize;
                if (!Has(bytes)) return false;
                _pos += (int)bytes;
                return true;
            }

            int size = ScalarSize(type);
            if (size == 0 || !Has((ulong)size)) return false;
            _pos += size;
            return true;
        }

        private static int ScalarSize(uint type) => type switch
        {
            0 or 1 or 7 => 1,      // u8, i8, bool
            2 or 3 => 2,           // u16, i16
            4 or 5 or 6 => 4,      // u32, i32, f32
            10 or 11 or 12 => 8,   // u64, i64, f64
            _ => 0
        };
    }
}
