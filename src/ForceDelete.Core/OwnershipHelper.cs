using System.Security.AccessControl;
using System.Security.Principal;

namespace ForceDelete.Core;

/// <summary>
/// Takes ownership (SetOwner) then adds an Allow-FullControl ACE for the current
/// user. Requires SeTakeOwnershipPrivilege to be enabled (see PrivilegeManager)
/// when the current user does not already own the target.
/// </summary>
public sealed class OwnershipHelper : IOwnershipHelper
{
    public void TakeOwnershipAndGrantFullControl(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("Cannot resolve current user SID.");

        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);

            // Set owner in its own call: only the modified section is applied.
            var ownerAcl = new DirectorySecurity();
            ownerAcl.SetOwner(user);
            di.SetAccessControl(ownerAcl);

            var acl = di.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(acl);
        }
        else
        {
            var fi = new FileInfo(path);

            var ownerAcl = new FileSecurity();
            ownerAcl.SetOwner(user);
            fi.SetAccessControl(ownerAcl);

            var acl = fi.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(acl);
        }
    }
}
