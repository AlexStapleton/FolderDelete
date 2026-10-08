using System.Security.AccessControl;
using System.Security.Principal;
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class OwnershipHelperTests
{
    [Fact]
    public void File_GrantsCurrentUserFullControl()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("owned.txt");

        new OwnershipHelper().TakeOwnershipAndGrantFullControl(file);

        var sid = WindowsIdentity.GetCurrent().User!;
        var rules = new FileInfo(file).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier));

        bool hasFullControl = rules
            .Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(sid)
                   && r.AccessControlType == AccessControlType.Allow
                   && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));

        Assert.True(hasFullControl);
    }

    [Fact]
    public void Directory_GrantsCurrentUserFullControl()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("owned");

        new OwnershipHelper().TakeOwnershipAndGrantFullControl(dir);

        var sid = WindowsIdentity.GetCurrent().User!;
        var rules = new DirectoryInfo(dir).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier));

        bool hasFullControl = rules
            .Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(sid)
                   && r.AccessControlType == AccessControlType.Allow
                   && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));

        Assert.True(hasFullControl);
    }
}
