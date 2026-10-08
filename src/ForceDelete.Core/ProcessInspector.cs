using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ForceDelete.Core;

/// <summary>What could be learned about a live process.</summary>
/// <param name="Accessible">False when the process could not be opened even for a limited query.</param>
public sealed record ProcessSnapshot(string ExeName, DateTime? StartTimeUtc, bool IsCritical, bool Accessible);

/// <summary>
/// Queries live processes through a limited-information handle (works for nearly every
/// process when elevated, including protected ones).
/// </summary>
public static class ProcessInspector
{
    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint SYNCHRONIZE = 0x00100000;
    internal const int ERROR_INVALID_PARAMETER = 87; // OpenProcess: no such process

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process,
        out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(SafeProcessHandle process,
        [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
        StringBuilder exeName, ref int size);

    /// <summary>
    /// Returns false if the process no longer exists. Otherwise returns a snapshot; an
    /// inaccessible process yields Accessible = false and must be treated as protected.
    /// </summary>
    public static bool TryInspect(int pid, out ProcessSnapshot snapshot)
    {
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid)
        {
            snapshot = new ProcessSnapshot(string.Empty, null, IsCritical: false, Accessible: false);
            return Marshal.GetLastPInvokeError() != ERROR_INVALID_PARAMETER;
        }

        snapshot = new ProcessSnapshot(ExeName(h), StartTimeUtc(h), IsCritical(h), Accessible: true);
        return true;
    }

    internal static DateTime? StartTimeUtc(SafeProcessHandle h) =>
        GetProcessTimes(h, out long created, out _, out _, out _)
            ? DateTime.FromFileTimeUtc(created)
            : null;

    /// <summary>Fails closed: if criticality can't be read, assume the worst.</summary>
    internal static bool IsCritical(SafeProcessHandle h) =>
        !IsProcessCritical(h, out bool critical) || critical;

    private static string ExeName(SafeProcessHandle h)
    {
        var buffer = new StringBuilder(1024);
        int size = buffer.Capacity;
        return QueryFullProcessImageName(h, 0, buffer, ref size)
            ? Path.GetFileNameWithoutExtension(buffer.ToString())
            : string.Empty;
    }
}
