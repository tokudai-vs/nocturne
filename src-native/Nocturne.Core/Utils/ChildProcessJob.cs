using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nocturne.Core.Utils;

// Helper processes (mpv, ffmpeg, cloudflared) are put in one Windows Job Object created with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. The handle is held for the life of this process and closed by Windows when
// it exits for any reason, crash or kill included, which terminates every process still in the job. Without it,
// ffmpeg kept pulling the stream from Emby and cloudflared kept the tunnel open after Nocturne died.
public static class ChildProcessJob
{
    private static readonly Lazy<SafeFileHandle?> AppJob = new(() => OperatingSystem.IsWindows() ? Create() : null);

    public static void Add(Process process)
    {
        var job = AppJob.Value;
        if (job == null || job.IsInvalid) return;
        if (!AssignProcessToJobObject(job, process.Handle))
            NocturneLogger.Warn("ChildProcessJob", $"Could not add {process.ProcessName} (pid {process.Id}) to the kill-on-close job: error {Marshal.GetLastWin32Error()}");
    }

    internal static SafeFileHandle? Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            NocturneLogger.Warn("ChildProcessJob", $"CreateJobObject failed: error {Marshal.GetLastWin32Error()}");
            return null;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            NocturneLogger.Warn("ChildProcessJob", $"SetInformationJobObject failed: error {Marshal.GetLastWin32Error()}");
            handle.Dispose();
            return null;
        }
        return handle;
    }

    internal static bool Assign(SafeFileHandle job, Process process) => AssignProcessToJobObject(job, process.Handle);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

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
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
