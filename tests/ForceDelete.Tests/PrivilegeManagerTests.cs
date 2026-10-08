using ForceDelete.Core;

namespace ForceDelete.Tests;

public class PrivilegeManagerTests
{
    [Fact]
    public void Enable_KnownAvailablePrivilege_ReturnsTrue()
    {
        // Every user holds SeChangeNotifyPrivilege.
        Assert.True(PrivilegeManager.Enable("SeChangeNotifyPrivilege"));
    }

    [Fact]
    public void Enable_UnknownPrivilege_ReturnsFalse()
    {
        Assert.False(PrivilegeManager.Enable("SeThisIsNotARealPrivilege"));
    }

    [Fact]
    public void EnableDeletePrivileges_ReportsWhatCouldNotBeEnabled()
    {
        // Unelevated, these privileges are absent from the token: report them, don't ignore.
        IReadOnlyList<string> missing = PrivilegeManager.EnableDeletePrivileges();
        Assert.All(missing, name => Assert.StartsWith("Se", name));
    }
}
