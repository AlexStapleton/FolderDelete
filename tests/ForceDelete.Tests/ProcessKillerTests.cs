using System.Diagnostics;
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class ProcessKillerTests
{
    private static Process StartPing() =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

    [Fact]
    public void Kills_WhenStartTimeMatches()
    {
        using var p = StartPing();
        try
        {
            var target = new LockingProcess(p.Id, "ping", false, p.StartTime.ToUniversalTime());
            Assert.True(new ProcessKiller().Kill(target));
            Assert.True(p.WaitForExit(2000));
        }
        finally { if (!p.HasExited) p.Kill(); }
    }

    [Fact]
    public void Refuses_WhenPidWasReused()
    {
        using var p = StartPing();
        try
        {
            // Same PID, different start time = a different process now owns that PID.
            var stale = new LockingProcess(p.Id, "ping", false, p.StartTime.ToUniversalTime().AddMinutes(-5));
            Assert.False(new ProcessKiller().Kill(stale));
            Assert.False(p.HasExited);
        }
        finally { if (!p.HasExited) p.Kill(); }
    }

    [Fact]
    public void AlreadyExited_CountsAsGone()
    {
        int pid;
        DateTime start;
        using (var p = StartPing())
        {
            pid = p.Id;
            start = p.StartTime.ToUniversalTime();
            p.Kill();
            p.WaitForExit();
        }
        Assert.True(new ProcessKiller().Kill(new LockingProcess(pid, "ping", false, start)));
    }
}
