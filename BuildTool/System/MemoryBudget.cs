using System.Runtime.InteropServices;

namespace Beast.BuildTool;

public static class MemoryBudget
{
    [StructLayout(LayoutKind.Sequential)]
    private sealed class Status
    {
        public uint Length = (uint)Marshal.SizeOf<Status>();
        public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] Status status);
    public static long AvailableMB()
    {
        if (!OperatingSystem.IsWindows()) return Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        var status = new Status();
        if (!GlobalMemoryStatusEx(status)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return (long)(status.AvailablePhysical / (1024 * 1024));
    }
    public static int Cost(CommandContext action, AccelerationConfig config) => action.Type == CommandType.Copy ? 1 : action.Type == CommandType.Link ? config.LinkMemoryMB : config.CompileMemoryMB;
}
