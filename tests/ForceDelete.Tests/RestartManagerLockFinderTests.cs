using System.Diagnostics;
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class RestartManagerLockFinderTests
{
    [Fact]
    public void ReportsCurrentProcess_WhenFileHeldOpen()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("held.bin");

        using (var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var lockers = new RestartManagerLockFinder().FindLockers(file);
            Assert.Contains(lockers, p => p.Pid == Environment.ProcessId);
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
