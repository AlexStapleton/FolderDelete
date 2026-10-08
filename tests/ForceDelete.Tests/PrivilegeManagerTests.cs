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
    public void EnableDeletePrivileges_DoesNotThrow()
    {
        PrivilegeManager.EnableDeletePrivileges(); // may be no-op when not elevated
    }
}
