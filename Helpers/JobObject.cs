using System.Runtime.InteropServices;

namespace PRImager.AIAssistant.Helpers;

/// <summary>
/// Asigna procesos hijos a un Job Object de Windows.
/// Si el proceso padre muere, todos los hijos asignados se terminan automáticamente.
/// </summary>
public class JobObject : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _handle;
    private bool _disposed;

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    public JobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int infoSize = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, infoPtr, (uint)infoSize))
                throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    public void AssignProcess(System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new System.ComponentModel.Win32Exception();
    }

    public void Dispose()
    {
        if (!_disposed && _handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
            _disposed = true;
        }
    }
}
