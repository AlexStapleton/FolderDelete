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

    [Fact]
    public void ExplicitDenyAces_AreRemoved()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("denied.txt");
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var acl = new FileInfo(file).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Delete, AccessControlType.Deny));
        new FileInfo(file).SetAccessControl(acl);

        new OwnershipHelper().TakeOwnershipAndGrantFullControl(file);

        var rules = new FileInfo(file).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        Assert.DoesNotContain(rules, r => r.AccessControlType == AccessControlType.Deny);
    }

    [Fact]
    public void Junction_DoesNotChangeTargetSecurity()
    {
        using var ws = new TestWorkspace();
        var target = ws.CreateDir("target");
        ws.CreateJunction("link", target);
        string Sddl() => new DirectoryInfo(target).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        var before = Sddl();

        new OwnershipHelper().TakeOwnershipAndGrantFullControl(ws.Path("link"));

        Assert.Equal(before, Sddl());
    }

    [Fact]
    public void TrailingDotName_IsRepairedRatherThanNotFound()
    {
        using var ws = new TestWorkspace();
        var file = Path.Combine(ws.Root, "bad.");
        File.WriteAllText(@"\\?\" + file, "x");

        new OwnershipHelper().TakeOwnershipAndGrantFullControl(file); // must not throw
    }
}
