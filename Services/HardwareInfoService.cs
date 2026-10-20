using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PRImager.AIAssistant.Services;

/// <summary>Una placa de video vista por Windows, de cualquier marca.</summary>
public sealed class GpuAdapterInfo
{
    public string Name { get; init; } = "";
    public uint VendorId { get; init; }

    /// <summary>Memoria de video propia, en MB. En una integrada es chica o cero: usa la RAM.</summary>
    public int DedicatedVramMb { get; init; }

    public string Vendor => VendorId switch
    {
        HardwareInfoService.VendorNvidia => "NVIDIA",
        HardwareInfoService.VendorAmd => "AMD",
        HardwareInfoService.VendorIntel => "Intel",
        _ => $"0x{VendorId:X4}"
    };

    /// <summary>Por debajo de 1 GB propio se la trata como integrada: no conviene cargarle un modelo.</summary>
    public bool IsDiscrete => DedicatedVramMb >= 1024;
}

/// <summary>Con qué backend de GPU vino compilado el llama-server que está al lado de la aplicación.</summary>
public enum ChatBackend
{
    CpuOnly,
    Cuda,
    Vulkan,
    Hip,
    Sycl
}

/// <summary>
/// Lo que tiene la PC, para recomendar cómo correr un modelo en ella. Valores en MB.
/// </summary>
public sealed class HardwareInfo
{
    public int TotalRamMb { get; init; }
    public int AvailableRamMb { get; init; }
    public int PhysicalCores { get; init; }
    public int LogicalCores { get; init; }
    public string CpuName { get; init; } = "";
    public IReadOnlyList<GpuAdapterInfo> Gpus { get; init; } = Array.Empty<GpuAdapterInfo>();
    public ChatBackend Backend { get; init; }

    /// <summary>
    /// La placa que el motor de chat puede usar de verdad, o null. No alcanza con tenerla:
    /// el llama-server con CUDA no usa una AMD, y entonces la PC corre como si no tuviera.
    /// </summary>
    public GpuAdapterInfo? UsableGpu { get; init; }

    /// <summary>VRAM libre medida, si se pudo (hoy solo con nvidia-smi). Si no, null.</summary>
    public int? FreeVramMb { get; init; }

    /// <summary>Por qué la placa no se usa, cuando hay placa y no se usa.</summary>
    public string? GpuNotUsableReason { get; init; }

    public int UsableVramMb => UsableGpu?.DedicatedVramMb ?? 0;

    /// <summary>Resumen de una línea para mostrar.</summary>
    public string Summary
    {
        get
        {
            string ram = $"{TotalRamMb / 1024.0:F0} GB de RAM";
            string cpu = PhysicalCores > 0 ? $"{PhysicalCores} núcleos" : $"{LogicalCores} hilos";
            string gpu = UsableGpu != null
                ? $"{UsableGpu.Name} · {UsableGpu.DedicatedVramMb / 1024.0:F0} GB de VRAM"
                : GpuNotUsableReason ?? "sin placa de video utilizable: corre en CPU";
            return $"{gpu} · {ram} · {cpu}";
        }
    }
}

/// <summary>
/// Detecta la PC sin depender de la marca de la placa (etapa E5, «recomendar según la PC»).
///
/// <see cref="GpuInfoService"/> pregunta a <c>nvidia-smi</c>, que en una PC con AMD, con
/// Intel o sin placa no existe: ahí el semáforo quedaba gris y no se recomendaba nada.
/// Acá la placa se lee de DXGI, que es lo que Windows usa para cualquier marca, y la RAM y
/// los núcleos del propio Windows. <c>nvidia-smi</c> se sigue usando, pero solo para lo que
/// DXGI no da: cuánta VRAM está libre en este momento.
///
/// No es código de red: se compila también en la configuración Forense.
/// </summary>
public class HardwareInfoService
{
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorAmd = 0x1002;
    public const uint VendorIntel = 0x8086;
    private const uint VendorMicrosoft = 0x1414; // «Microsoft Basic Render Driver»: software

    private readonly string _serverDirectory;
    private readonly GpuInfoService _nvidia;
    private HardwareInfo? _cached;

    /// <param name="serverDirectory">Carpeta de llama-server.exe: de sus .dll sale el backend.</param>
    public HardwareInfoService(string serverDirectory, GpuInfoService? nvidia = null)
    {
        _serverDirectory = serverDirectory;
        _nvidia = nvidia ?? new GpuInfoService();
    }

    public async Task<HardwareInfo> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_cached != null && !forceRefresh) return _cached;

        var gpus = ReadGpus();
        var backend = DetectBackend(_serverDirectory);
        var (usable, reason) = PickUsableGpu(gpus, backend);

        int? free = null;
        if (usable?.VendorId == VendorNvidia)
        {
            var smi = await _nvidia.GetVramAsync(forceRefresh, ct).ConfigureAwait(false);
            if (smi.Available) free = smi.FreeMb;
        }

        var (total, available) = ReadRam();
        var (physical, logical) = ReadCores();

        _cached = new HardwareInfo
        {
            TotalRamMb = total,
            AvailableRamMb = available,
            PhysicalCores = physical,
            LogicalCores = logical,
            CpuName = ReadCpuName(),
            Gpus = gpus,
            Backend = backend,
            UsableGpu = usable,
            FreeVramMb = free,
            GpuNotUsableReason = reason
        };
        return _cached;
    }

    /// <summary>
    /// El backend lo deciden las .dll que trae el llama-server de la aplicación: la misma
    /// release se publica en variantes CUDA, Vulkan, HIP y SYCL, y solo una está instalada.
    /// </summary>
    public static ChatBackend DetectBackend(string serverDirectory)
    {
        bool Has(string dll) => File.Exists(Path.Combine(serverDirectory, dll));

        if (Has("ggml-cuda.dll")) return ChatBackend.Cuda;
        if (Has("ggml-vulkan.dll")) return ChatBackend.Vulkan;
        if (Has("ggml-hip.dll")) return ChatBackend.Hip;
        if (Has("ggml-sycl.dll")) return ChatBackend.Sycl;
        return ChatBackend.CpuOnly;
    }

    /// <summary>
    /// La placa que el backend puede usar: la discreta de más memoria entre las compatibles.
    /// Público para poder ejercer la regla sin el hardware de cada caso.
    /// </summary>
    public static (GpuAdapterInfo? gpu, string? reason) PickUsableGpu(
        IReadOnlyList<GpuAdapterInfo> gpus, ChatBackend backend)
    {
        var discrete = gpus.Where(g => g.IsDiscrete).OrderByDescending(g => g.DedicatedVramMb).ToList();
        if (discrete.Count == 0)
            return (null, gpus.Count == 0
                ? "sin placa de video: corre en CPU"
                : $"{gpus[0].Name} es integrada: corre en CPU");

        bool Compatible(GpuAdapterInfo g) => backend switch
        {
            ChatBackend.Cuda => g.VendorId == VendorNvidia,
            ChatBackend.Hip => g.VendorId == VendorAmd,
            ChatBackend.Sycl => g.VendorId == VendorIntel,
            ChatBackend.Vulkan => true,
            _ => false
        };

        var usable = discrete.FirstOrDefault(Compatible);
        if (usable != null) return (usable, null);

        var best = discrete[0];
        string reason = backend == ChatBackend.CpuOnly
            ? $"{best.Name} no se usa: el motor de chat instalado es solo CPU"
            : $"{best.Name} no se usa: el motor de chat instalado es para {BackendName(backend)}, que no la soporta. Corre en CPU";
        return (null, reason);
    }

    public static string BackendName(ChatBackend b) => b switch
    {
        ChatBackend.Cuda => "CUDA (NVIDIA)",
        ChatBackend.Vulkan => "Vulkan",
        ChatBackend.Hip => "ROCm (AMD)",
        ChatBackend.Sycl => "SYCL (Intel)",
        _ => "CPU"
    };

    // --- RAM y CPU ---

    private static (int total, int available) ReadRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return ((int)(status.ullTotalPhys / (1024 * 1024)), (int)(status.ullAvailPhys / (1024 * 1024)));
    }

    /// <summary>
    /// Núcleos físicos: llama.cpp rinde mejor con un hilo por núcleo físico que con uno
    /// por hilo lógico, porque los dos hilos de un núcleo compiten por la misma unidad.
    /// </summary>
    private static (int physical, int logical) ReadCores()
    {
        int logical = Environment.ProcessorCount;
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return (0, logical);

            IntPtr buffer = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref len))
                    return (0, logical);

                int cores = 0;
                long offset = 0;
                while (offset < len)
                {
                    // Cada registro empieza con Relationship (u32) y Size (u32)
                    uint size = (uint)Marshal.ReadInt32(buffer, (int)offset + 4);
                    if (size == 0) break;
                    cores++;
                    offset += size;
                }
                return (cores, logical);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            return (0, logical);
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // --- placas, por DXGI ---

    /// <summary>
    /// Las placas que ve Windows. Si DXGI falla —un servidor sin escritorio, por ejemplo—
    /// devuelve la lista vacía: la PC se trata como sin placa, que es lo prudente.
    /// </summary>
    public static IReadOnlyList<GpuAdapterInfo> ReadGpus()
    {
        var result = new List<GpuAdapterInfo>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out object obj) != 0) return result;
            var factory = (IDXGIFactory1)obj;
            try
            {
                for (uint i = 0; ; i++)
                {
                    if (factory.EnumAdapters1(i, out IDXGIAdapter1 adapter) != 0) break;
                    try
                    {
                        adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc);
                        bool software = (desc.Flags & DxgiAdapterFlagSoftware) != 0 || desc.VendorId == VendorMicrosoft;
                        if (software) continue;

                        result.Add(new GpuAdapterInfo
                        {
                            Name = desc.Description.Trim(),
                            VendorId = desc.VendorId,
                            DedicatedVramMb = (int)((ulong)desc.DedicatedVideoMemory / (1024 * 1024))
                        });
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            // DXGI no disponible: sin placa
        }
        return result;
    }

    // --- interop ---

    private const int RelationProcessorCore = 0;
    private const uint DxgiAdapterFlagSoftware = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // Las interfaces COM se declaran con TODOS los métodos en el orden de la vtable,
    // aunque solo se llamen dos: el lugar de cada método es su posición.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        void GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
