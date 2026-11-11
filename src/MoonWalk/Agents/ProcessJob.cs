using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MoonWalk.Agents;

/// <summary>
/// A Windows job object that kills every process in it when MoonWalk exits, however it exits (even a hard kill). Agent and verify
/// processes are added to it, so they cannot be left running, editing the repository, after MoonWalk is gone. Children of a process in
/// the job join it automatically. On other platforms, or if the job cannot be created, this does nothing and the cancel path
/// (<c>Process.Kill(entireProcessTree)</c>) is the only cleanup.
/// </summary>
internal static class ProcessJob
{
    private const uint KillOnJobClose = 0x2000;
    private const int InfoClassExtendedLimit = 9;

    private static readonly nint Handle = Create();

    public static bool Available => Handle != 0;

    public static bool TryAssign(Process process)
    {
        if (Handle == 0)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(Handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // the process already exited
        }
    }

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            return 0;
        }

        var info = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, InfoClassExtendedLimit, buffer, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job; // intentionally never closed: it must live exactly as long as this process
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
