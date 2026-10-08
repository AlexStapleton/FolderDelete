using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ForceDelete.Core;

/// <summary>
/// Takes ownership, then REPLACES the DACL with Full Control for the current user,
/// Administrators and SYSTEM. Replacing (rather than adding an Allow ACE) matters:
/// explicit Deny ACEs are evaluated first and would otherwise still block the delete.
///
/// Works on a handle opened with FILE_OPEN_REPARSE_POINT, so a junction/symlink's
/// own security is changed — never its target's. Uses SetKernelObjectSecurity, which
/// (unlike the name-based APIs behind .NET's SetAccessControl) does not walk the whole
/// subtree re-propagating inheritance. Requires SeTakeOwnershipPrivilege (see
/// PrivilegeManager) when the current user does not already own the target.
/// </summary>
public sealed class OwnershipHelper : IOwnershipHelper
{
    private const uint WRITE_DAC = 0x00040000;
    private const uint WRITE_OWNER = 0x00080000;
    private const uint FILE_SHARE_ALL = 0x7; // read | write | delete
    private const uint FILE_OPEN_FOR_BACKUP_INTENT = 0x00004000; // honours SeBackup/SeRestore; opens directories
    private const uint FILE_OPEN_REPARSE_POINT = 0x00200000;
    private const uint OBJ_CASE_INSENSITIVE = 0x40;
    private const uint OWNER_SECURITY_INFORMATION = 0x1;
    private const uint DACL_SECURITY_INFORMATION = 0x4;
    private const uint PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK { public IntPtr Status; public IntPtr Information; }

    // NtOpenFile rather than CreateFile: CreateFile silently adds FILE_READ_ATTRIBUTES to
    // every request, so an item whose ACL denies reading its attributes could never be
    // opened to repair it. NtOpenFile asks for exactly WRITE_OWNER / WRITE_DAC.
    [DllImport("ntdll.dll")]
    private static extern int NtOpenFile(out SafeFileHandle handle, uint desiredAccess,
        ref OBJECT_ATTRIBUTES objectAttributes, out IO_STATUS_BLOCK ioStatusBlock,
        uint shareAccess, uint openOptions);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation,
        byte[] securityDescriptor);

    public void TakeOwnershipAndGrantFullControl(string path)
    {
        var ext = PathUtil.ToExtendedPath(path);
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("Cannot resolve current user SID.");

        // 1. Owner. May legitimately fail when we already own it but lack the privilege;
        //    the DACL step then decides — and reports this error if it can't proceed.
        Win32Exception? ownerError = null;
        try
        {
            using var h = Open(ext, WRITE_OWNER);
            if (!SetKernelObjectSecurity(h, OWNER_SECURITY_INFORMATION, OwnerDescriptor(user)))
                ownerError = LastError();
        }
        catch (Win32Exception ex)
        {
            ownerError = ex;
        }

        // 2. DACL. The owner is implicitly granted WRITE_DAC.
        SafeFileHandle dacHandle;
        try
        {
            dacHandle = Open(ext, WRITE_DAC);
        }
        catch (Win32Exception ex)
        {
            throw ownerError ?? ex;
        }
        using (dacHandle)
        {
            if (!SetKernelObjectSecurity(dacHandle,
                    DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, DaclDescriptor(user)))
                throw LastError();
        }
    }

    /// <summary>Opens the item itself (never a reparse target) for exactly the given access.</summary>
    private static SafeFileHandle Open(string extendedPath, uint access)
    {
        if (!extendedPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ArgumentException($"Path must be fully qualified: {extendedPath}");

        // \\?\C:\x -> \??\C:\x and \\?\UNC\s\x -> \??\UNC\s\x (the NT object-manager form).
        var ntPath = @"\??\" + extendedPath.Substring(4);

        var buffer = Marshal.StringToHGlobalUni(ntPath);
        var name = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
        try
        {
            Marshal.StructureToPtr(new UNICODE_STRING
            {
                Length = (ushort)(ntPath.Length * 2),
                MaximumLength = (ushort)(ntPath.Length * 2 + 2),
                Buffer = buffer
            }, name, false);

            var attributes = new OBJECT_ATTRIBUTES
            {
                Length = Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
                ObjectName = name,
                Attributes = OBJ_CASE_INSENSITIVE
            };

            int status = NtOpenFile(out var handle, access, ref attributes, out _, FILE_SHARE_ALL,
                FILE_OPEN_FOR_BACKUP_INTENT | FILE_OPEN_REPARSE_POINT);
            if (status < 0)
            {
                handle.Dispose();
                throw new Win32Exception(RtlNtStatusToDosError(status));
            }
            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Win32Exception LastError() => new(Marshal.GetLastPInvokeError());

    private static byte[] OwnerDescriptor(SecurityIdentifier owner) =>
        Binary(new RawSecurityDescriptor(ControlFlags.None, owner, null, null, null));

    private static byte[] DaclDescriptor(SecurityIdentifier user)
    {
        var dacl = new RawAcl(GenericAcl.AclRevision, 3);
        int index = 0;
        foreach (var sid in new[] { user, Administrators, LocalSystem }.Distinct())
            dacl.InsertAce(index++, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                (int)FileSystemRights.FullControl, sid, false, null));

        return Binary(new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            null, null, null, dacl));
    }

    private static byte[] Binary(GenericSecurityDescriptor sd)
    {
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
