using System.Diagnostics;

namespace ForceDelete.Core;

public sealed class ProcessKiller : IProcessKiller
{
    public bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return p.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch
        {
            return false;
        }
    }
}
