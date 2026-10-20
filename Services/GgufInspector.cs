using System;
using System.Buffers.Binary;
using System.IO;
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
    public static GgufHeader? TryReadFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(ProbeBytes, fs.Length)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            return TryParse(buffer.AsSpan(0, read));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parsea el comienzo de un GGUF. Tolera que el bloque termine a mitad de la metadata:
    /// devuelve lo que alcanzó a leer, que es lo de arriba, lo único que importa acá.
    /// Null si ni siquiera es un GGUF.
    /// </summary>
    public static GgufHeader? TryParse(ReadOnlySpan<byte> data)
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

        var r = new Reader(data, 24);
        for (ulong i = 0; i < kvCount; i++)
        {
            if (!r.TryString(out string key) || !r.TryU32(out uint type)) break;

            // block_count es lo único que se necesita de la sección de la arquitectura,
            // y su nombre lleva la arquitectura adelante: llama.block_count, qwen35.block_count
            bool wanted = key is "general.architecture" or "general.type" or "general.name"
                       or "general.size_label" or "split.count" or "split.no"
                       || key.EndsWith(".block_count", StringComparison.Ordinal) && !key.StartsWith("clip.", StringComparison.Ordinal);

            if (!wanted)
            {
                if (!r.TrySkip(type)) break;
                continue;
            }

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
            }
            else
            {
                if (!r.TryInteger(type, out ulong n)) break;
                if (key == "split.count") header.SplitCount = n;
                else if (key == "split.no") header.SplitNo = n;
                else header.BlockCount ??= n;
            }
        }

        return header;
    }

    // --- veredictos: el texto lo lee el usuario ---

    private static GgufVerdict SplitVerdict(ulong part, ulong total) => new()
    {
        Kind = GgufKind.SplitPart,
        Reason = $"Es la parte {part} de {total} de un modelo partido en varios archivos. " +
                 "Sola no carga, y la aplicación todavía no baja modelos partidos."
    };

    private static GgufVerdict ProjectorVerdict() => new()
    {
        Kind = GgufKind.Projector,
        Reason = "Es un proyector de visión (mmproj), no un modelo. Se baja junto al modelo " +
                 "del mismo repositorio para que pueda ver imágenes; solo no hace nada."
    };

    private static GgufVerdict DraftVerdict(GgufHeader? h) => new()
    {
        Kind = GgufKind.DraftHead,
        Reason = "Es una cabeza de borrador para acelerar al modelo grande del mismo " +
                 "repositorio, no un modelo: no carga sola." +
                 (h?.BlockCount is { } b
                     ? $" Declara {b} capas y trae {h.TensorCount} tensores; un modelo completo trae varios por capa."
                     : "")
    };

    private static GgufVerdict AdapterVerdict() => new()
    {
        Kind = GgufKind.Adapter,
        Reason = "Es un adaptador LoRA: modifica a un modelo base, no carga solo."
    };

    private static GgufVerdict VocabVerdict() => new()
    {
        Kind = GgufKind.VocabOnly,
        Reason = "Trae solo el vocabulario, sin los pesos del modelo: no carga."
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

        /// <summary>Lee un entero sin signo de cualquier ancho; los conteos vienen en u32 o u64 según quién convirtió.</summary>
        public bool TryInteger(uint type, out ulong value)
        {
            value = 0;
            int size = ScalarSize(type);
            if (size == 0 || type is 6 or 7 or 12 || !Has((ulong)size)) return false;
            var s = _data.Slice(_pos, size);
            value = size switch
            {
                1 => s[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                _ => BinaryPrimitives.ReadUInt64LittleEndian(s)
            };
            _pos += size;
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
