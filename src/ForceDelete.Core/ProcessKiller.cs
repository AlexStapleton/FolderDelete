using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForceDelete.Core;

/// <summary>
/// Terminates exactly the reported process — not its child tree, which the user never saw.
/// The identity check and the termination use one handle, so the PID cannot be recycled
/// in between.
/// </summary>
public sealed class ProcessKiller : IProcessKiller
{
    private const uint WAIT_OBJECT_0 = 0;
    private const uint ExitWaitMs = 5000;
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromMilliseconds(10);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    public bool Kill(LockingProcess process)
    {
        using var h = ProcessInspector.OpenProcess(
            ProcessInspector.PROCESS_TERMINATE | ProcessInspector.SYNCHRONIZE |
            ProcessInspector.PROCESS_QUERY_LIMITED_INFORMATION,
            false, process.Pid);
        if (h.IsInvalid)
            return Marshal.GetLastPInvokeError() == ProcessInspector.ERROR_INVALID_PARAMETER; // already gone

        // A different start time means the original exited and its PID was reused.
        if (process.StartTimeUtc is { } expected)
        {
            var actual = ProcessInspector.StartTimeUtc(h);
            if (actual is null || (actual.Value - expected).Duration() > StartTimeTolerance)
                return false;
        }

        // Exited on its own (the handle can outlive the process): nothing to kill.
        if (WaitForSingleObject(h, 0) == WAIT_OBJECT_0) return true;

        // Last line of defence: terminating a critical process bugchecks the machine.
        if (ProcessInspector.IsCritical(h)) return false;

        if (!TerminateProcess(h, 1)) return false;
        return WaitForSingleObject(h, ExitWaitMs) == WAIT_OBJECT_0;
    }
}
