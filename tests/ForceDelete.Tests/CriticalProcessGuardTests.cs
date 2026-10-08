using ForceDelete.Core;

namespace ForceDelete.Tests;

public class CriticalProcessGuardTests
{
    [Theory]
    [InlineData(4, "System")]
    [InlineData(0, "System Idle Process")]
    [InlineData(900, "csrss.exe")]
    [InlineData(901, "wininit")]
    [InlineData(902, "lsass.exe")]
    [InlineData(903, "services.exe")]
    [InlineData(904, "smss.exe")]
    [InlineData(905, "winlogon.exe")]
    public void KnownCritical_ReturnsTrue(int pid, string name)
    {
        Assert.True(CriticalProcessGuard.IsCritical(pid, name));
    }

    [Theory]
    [InlineData(4321, "notepad.exe")]
    [InlineData(5000, "chrome.exe")]
    [InlineData(6000, "MyApp")]
    public void OrdinaryProcess_ReturnsFalse(int pid, string name)
    {
        Assert.False(CriticalProcessGuard.IsCritical(pid, name));
    }
}
