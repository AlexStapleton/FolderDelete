using System.Diagnostics;
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class RestartManagerLockFinderTests
{
    [Fact]
    public void ReportsCurrentProcess_WhenFileHeldOpen_AsUnkillable()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("held.bin");

        using (var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockers = new RestartManagerLockFinder().FindLockers(file);
            var me = Assert.Single(lockers, p => p.Pid == Environment.ProcessId);
            // Restart Manager flags the calling process RmCritical: we must never kill ourselves.
            Assert.True(me.IsCritical);
        }
    }

    [Fact]
    public void ReportsOtherProcess_RunningTheImage_WithIdentity()
    {
        using var ws = new TestWorkspace();
        var exe = ws.Path("held.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);

        using var proc = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            Thread.Sleep(300);
            var lockers = new RestartManagerLockFinder().FindLockers(exe);

            var holder = Assert.Single(lockers, p => p.Pid == proc.Id);
            Assert.False(holder.IsCritical);
            Assert.Null(holder.ServiceName);
            Assert.Equal(proc.StartTime.ToUniversalTime(), holder.StartTimeUtc!.Value, TimeSpan.FromMilliseconds(10));
            Assert.Contains("held", holder.Name, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!proc.HasExited) proc.Kill();
        }
    }

    [Fact]
    public void ReturnsEmpty_WhenFileNotHeld()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("free.bin");

        var lockers = new RestartManagerLockFinder().FindLockers(file);
        Assert.Empty(lockers);
    }
}
