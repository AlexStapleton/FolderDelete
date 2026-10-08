# ForceDelete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows 11 WinForms app that force-deletes any file/folder the user selects, escalating through read-only/hidden/system attributes, ACL denials (take ownership + grant full control), locked-by-process (Restart Manager), long paths, and reparse points.

**Architecture:** A testable `ForceDelete.Core` library holds all deletion logic behind small injectable seams (`ILockFinder`, `IProcessKiller`, `IKillDecision`, `IOwnershipHelper`, `IDeleteObserver`) plus `protected virtual` raw-delete methods, so the escalation logic is unit-tested with real temp files and fakes without needing admin during tests. A thin `ForceDelete.App` WinForms project provides the UI, self-elevates via manifest, and implements the seams against the real OS. `ForceDelete.Tests` (xUnit) covers Core.

**Tech Stack:** C# / .NET 9, `net9.0-windows` TFM, WinForms, xUnit, Win32 P/Invoke (advapi32 for token privileges, rstrtmgr for Restart Manager), `System.Security.AccessControl`.

> **Note on version control:** The user declined git for this project. Where a normal plan would `git commit`, each task instead ends by running the full test suite green (`dotnet test`). Do not run `git` commands.

> **Note on admin:** Tests are designed to pass WITHOUT running elevated. The genuinely admin-only behavior (taking ownership of a foreign-owned system file) is validated by manual verification in Task 14, not by unit tests.

---

## File Structure

```
ForceDelete.sln
src/
  ForceDelete.Core/
    ForceDelete.Core.csproj        # net9.0-windows, library
    Types.cs                       # ItemStatus, LogLevel, LockingProcess, interfaces
    PathUtil.cs                    # \\?\ extended-length prefixing
    AttributeHelper.cs             # clear ReadOnly/Hidden/System
    ReparsePointHelper.cs          # detect junction/symlink/mount
    PrivilegeManager.cs            # enable SeTakeOwnership/SeRestore privileges
    CriticalProcessGuard.cs        # is a PID/name unsafe to kill
    OwnershipHelper.cs             # IOwnershipHelper + real impl (SetOwner + grant)
    RestartManagerLockFinder.cs    # ILockFinder real impl (rstrtmgr)
    ProcessKiller.cs               # IProcessKiller real impl
    DeleteEngine.cs                # orchestration; virtual RawDelete* seams
  ForceDelete.App/
    ForceDelete.App.csproj         # net9.0-windows, WinForms, single-file publish
    app.manifest                   # requireAdministrator + longPathAware
    Program.cs                     # entry point
    MainForm.cs                    # UI + wiring, implements IDeleteObserver/IKillDecision
tests/
  ForceDelete.Tests/
    ForceDelete.Tests.csproj       # net9.0-windows, xUnit
    TestWorkspace.cs               # temp-dir fixture + junction helper
    Fakes.cs                       # recording/fake seams
    PathUtilTests.cs
    AttributeHelperTests.cs
    ReparsePointHelperTests.cs
    PrivilegeManagerTests.cs
    CriticalProcessGuardTests.cs
    OwnershipHelperTests.cs
    RestartManagerLockFinderTests.cs
    DeleteEngineHappyPathTests.cs
    DeleteEngineEscalationTests.cs
    DeleteEngineLockedTests.cs
```

---

## Task 1: Solution and project scaffold

**Files:**
- Create: `ForceDelete.sln`
- Create: `src/ForceDelete.Core/ForceDelete.Core.csproj`
- Create: `tests/ForceDelete.Tests/ForceDelete.Tests.csproj`
- Create: `tests/ForceDelete.Tests/SmokeTest.cs`

- [ ] **Step 1: Create the solution and projects**

Run from the repo root (`I:/Coding-Projects/FolderDelete`):

```bash
dotnet new sln -n ForceDelete
dotnet new classlib -n ForceDelete.Core -o src/ForceDelete.Core -f net9.0-windows
dotnet new xunit  -n ForceDelete.Tests -o tests/ForceDelete.Tests -f net9.0-windows
dotnet sln add src/ForceDelete.Core/ForceDelete.Core.csproj
dotnet sln add tests/ForceDelete.Tests/ForceDelete.Tests.csproj
dotnet add tests/ForceDelete.Tests/ForceDelete.Tests.csproj reference src/ForceDelete.Core/ForceDelete.Core.csproj
rm src/ForceDelete.Core/Class1.cs
rm tests/ForceDelete.Tests/UnitTest1.cs
```

- [ ] **Step 2: Set Core csproj properties**

Replace `src/ForceDelete.Core/ForceDelete.Core.csproj` contents with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Write a smoke test**

Create `tests/ForceDelete.Tests/SmokeTest.cs`:

```csharp
namespace ForceDelete.Tests;

public class SmokeTest
{
    [Fact]
    public void Harness_Runs()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 4: Build and run tests**

Run: `dotnet test`
Expected: build succeeds, 1 test passes.

---

## Task 2: Core types and interfaces

**Files:**
- Create: `src/ForceDelete.Core/Types.cs`

- [ ] **Step 1: Write the types and seams**

Create `src/ForceDelete.Core/Types.cs`:

```csharp
namespace ForceDelete.Core;

/// <summary>Per-item lifecycle status shown in the queue.</summary>
public enum ItemStatus
{
    Pending,
    Deleting,
    FixingPermissions,
    Locked,
    Done,
    Failed
}

public enum LogLevel { Info, Action, Warning, Error }

/// <summary>A process reported by Restart Manager as holding a file open.</summary>
public sealed record LockingProcess(int Pid, string Name, bool IsCritical);

/// <summary>Receives status and log updates during deletion. UI implements this.</summary>
public interface IDeleteObserver
{
    void OnStatus(string path, ItemStatus status);
    void OnLog(string message, LogLevel level);
}

/// <summary>Finds which processes hold a file open.</summary>
public interface ILockFinder
{
    IReadOnlyList<LockingProcess> FindLockers(string path);
}

/// <summary>Terminates a process by id. Returns true if it is gone afterward.</summary>
public interface IProcessKiller
{
    bool Kill(int pid);
}

/// <summary>Asks whether to close the (already-vetted, non-critical) holding processes.</summary>
public interface IKillDecision
{
    bool ShouldKill(string path, IReadOnlyList<LockingProcess> processes);
}

/// <summary>Takes ownership and grants the current user full control of a path.</summary>
public interface IOwnershipHelper
{
    void TakeOwnershipAndGrantFullControl(string path);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/ForceDelete.Core/ForceDelete.Core.csproj`
Expected: build succeeds (no tests yet for this file).

---

## Task 3: PathUtil — extended-length prefixing

**Files:**
- Create: `src/ForceDelete.Core/PathUtil.cs`
- Create: `tests/ForceDelete.Tests/PathUtilTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/ForceDelete.Tests/PathUtilTests.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class PathUtilTests
{
    [Fact]
    public void ShortLocalPath_IsUnchanged()
    {
        Assert.Equal(@"C:\temp\file.txt", PathUtil.ToExtendedPath(@"C:\temp\file.txt"));
    }

    [Fact]
    public void AlreadyPrefixed_IsUnchanged()
    {
        Assert.Equal(@"\\?\C:\temp\file.txt", PathUtil.ToExtendedPath(@"\\?\C:\temp\file.txt"));
    }

    [Fact]
    public void LongLocalPath_GetsPrefix()
    {
        var longPath = @"C:\" + new string('a', 300);
        Assert.Equal(@"\\?\" + longPath, PathUtil.ToExtendedPath(longPath));
    }

    [Fact]
    public void LongUncPath_GetsUncPrefix()
    {
        var longUnc = @"\\server\share\" + new string('b', 300);
        var result = PathUtil.ToExtendedPath(longUnc);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('b', 300), result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PathUtilTests`
Expected: FAIL (PathUtil does not exist).

- [ ] **Step 3: Implement PathUtil**

Create `src/ForceDelete.Core/PathUtil.cs`:

```csharp
namespace ForceDelete.Core;

public static class PathUtil
{
    private const int LegacyMaxPath = 260;
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Returns the extended-length form for paths that need it (at/over the legacy
    /// limit), leaving already-prefixed or short paths untouched. The prefix disables
    /// path normalization, so it is applied selectively rather than universally.
    /// </summary>
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path;

        if (path.Length < LegacyMaxPath) return path;

        // UNC: \\server\share\... -> \\?\UNC\server\share\...
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return UncPrefix + path.Substring(2);

        return Prefix + path;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PathUtilTests`
Expected: PASS (4 tests).

---

## Task 4: AttributeHelper — clear blocking attributes

**Files:**
- Create: `src/ForceDelete.Core/AttributeHelper.cs`
- Create: `tests/ForceDelete.Tests/TestWorkspace.cs`
- Create: `tests/ForceDelete.Tests/AttributeHelperTests.cs`

- [ ] **Step 1: Create the temp-workspace helper**

Create `tests/ForceDelete.Tests/TestWorkspace.cs`:

```csharp
using System.Diagnostics;

namespace ForceDelete.Tests;

/// <summary>Creates a unique temp directory and best-effort deletes it on Dispose.</summary>
public sealed class TestWorkspace : IDisposable
{
    public string Root { get; }

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "fd_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public string CreateFile(string relative, string content = "x")
    {
        var full = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDir(string relative)
    {
        var full = Path(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a directory junction (does NOT require admin).</summary>
    public void CreateJunction(string linkRelative, string targetFull)
    {
        var link = Path(linkRelative);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{targetFull}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("mklink failed: " + p.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        try { ClearAttributesRecursive(Root); } catch { }
        try { Directory.Delete(Root, recursive: true); } catch { }
    }

    private static void ClearAttributesRecursive(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/ForceDelete.Tests/AttributeHelperTests.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class AttributeHelperTests
{
    [Fact]
    public void ClearsReadOnlyHiddenSystem_AllowingDelete()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("locked.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

        AttributeHelper.ClearBlockingAttributes(file);

        var attrs = File.GetAttributes(file);
        Assert.False(attrs.HasFlag(FileAttributes.ReadOnly));
        Assert.False(attrs.HasFlag(FileAttributes.Hidden));
        Assert.False(attrs.HasFlag(FileAttributes.System));

        File.Delete(file); // must not throw now
        Assert.False(File.Exists(file));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter AttributeHelperTests`
Expected: FAIL (AttributeHelper does not exist).

- [ ] **Step 4: Implement AttributeHelper**

Create `src/ForceDelete.Core/AttributeHelper.cs`:

```csharp
namespace ForceDelete.Core;

public static class AttributeHelper
{
    private const FileAttributes Blocking =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    /// <summary>Removes ReadOnly/Hidden/System so a delete can proceed. No-op if none set.</summary>
    public static void ClearBlockingAttributes(string path)
    {
        var attrs = File.GetAttributes(path);
        var cleared = attrs & ~Blocking;
        if (cleared != attrs)
            File.SetAttributes(path, cleared);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter AttributeHelperTests`
Expected: PASS.

---

## Task 5: ReparsePointHelper — detect junctions/symlinks

**Files:**
- Create: `src/ForceDelete.Core/ReparsePointHelper.cs`
- Create: `tests/ForceDelete.Tests/ReparsePointHelperTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/ForceDelete.Tests/ReparsePointHelperTests.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class ReparsePointHelperTests
{
    [Fact]
    public void NormalDirectory_IsNotReparsePoint()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("plain");
        Assert.False(ReparsePointHelper.IsReparsePoint(dir));
    }

    [Fact]
    public void NormalFile_IsNotReparsePoint()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("plain.txt");
        Assert.False(ReparsePointHelper.IsReparsePoint(file));
    }

    [Fact]
    public void Junction_IsReparsePoint()
    {
        using var ws = new TestWorkspace();
        var target = ws.CreateDir("target");
        ws.CreateJunction("link", target);
        Assert.True(ReparsePointHelper.IsReparsePoint(ws.Path("link")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReparsePointHelperTests`
Expected: FAIL (ReparsePointHelper does not exist).

- [ ] **Step 3: Implement ReparsePointHelper**

Create `src/ForceDelete.Core/ReparsePointHelper.cs`:

```csharp
namespace ForceDelete.Core;

public static class ReparsePointHelper
{
    /// <summary>
    /// True if the path is a junction, symlink, mount point, or other reparse point.
    /// Callers must delete the link itself and never recurse through it.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ReparsePointHelperTests`
Expected: PASS (3 tests).

---

## Task 6: PrivilegeManager — enable token privileges

**Files:**
- Create: `src/ForceDelete.Core/PrivilegeManager.cs`
- Create: `tests/ForceDelete.Tests/PrivilegeManagerTests.cs`

- [ ] **Step 1: Write the failing tests**

`SeChangeNotifyPrivilege` is enabled for every user, so `Enable` must return true for it; a bogus name must return false. This is deterministic without admin.

Create `tests/ForceDelete.Tests/PrivilegeManagerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PrivilegeManagerTests`
Expected: FAIL (PrivilegeManager does not exist).

- [ ] **Step 3: Implement PrivilegeManager**

Create `src/ForceDelete.Core/PrivilegeManager.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ForceDelete.Core;

/// <summary>
/// Enables token privileges. An elevated admin token CONTAINS SeTakeOwnership/
/// SeRestore but leaves them DISABLED by default; SetOwner fails until enabled.
/// </summary>
public static class PrivilegeManager
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privilege; }

    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const int ERROR_SUCCESS = 0;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Enables one privilege in the current process token. Returns true on success.</summary>
    public static bool Enable(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // AdjustTokenPrivileges returns true even when the privilege was not
            // assigned (ERROR_NOT_ALL_ASSIGNED); the last error is the real answer.
            return Marshal.GetLastWin32Error() == ERROR_SUCCESS;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>Enables the privileges needed to take ownership of foreign-owned files.</summary>
    public static void EnableDeletePrivileges()
    {
        Enable("SeTakeOwnershipPrivilege");
        Enable("SeRestorePrivilege");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PrivilegeManagerTests`
Expected: PASS (3 tests).

---

## Task 7: CriticalProcessGuard — unsafe-to-kill check

**Files:**
- Create: `src/ForceDelete.Core/CriticalProcessGuard.cs`
- Create: `tests/ForceDelete.Tests/CriticalProcessGuardTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/ForceDelete.Tests/CriticalProcessGuardTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CriticalProcessGuardTests`
Expected: FAIL (CriticalProcessGuard does not exist).

- [ ] **Step 3: Implement CriticalProcessGuard**

Create `src/ForceDelete.Core/CriticalProcessGuard.cs`:

```csharp
namespace ForceDelete.Core;

/// <summary>
/// Identifies processes that must never be terminated: killing them blue-screens
/// Windows or is impossible (the System process). Used to refuse the kill offer.
/// </summary>
public static class CriticalProcessGuard
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "csrss", "wininit", "smss", "services", "lsass", "winlogon",
        "system idle process"
    };

    public static bool IsCritical(int pid, string processName)
    {
        if (pid <= 4) return true; // 0 = Idle, 4 = System

        var name = processName ?? string.Empty;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return Names.Contains(name);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CriticalProcessGuardTests`
Expected: PASS.

---

## Task 8: OwnershipHelper — take ownership + grant full control

**Files:**
- Create: `src/ForceDelete.Core/OwnershipHelper.cs`
- Create: `tests/ForceDelete.Tests/OwnershipHelperTests.cs`

- [ ] **Step 1: Write the failing test**

On a file the test process owns, taking ownership + granting full control must succeed and leave an explicit Allow-FullControl ACE for the current user. Runs without admin.

Create `tests/ForceDelete.Tests/OwnershipHelperTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter OwnershipHelperTests`
Expected: FAIL (OwnershipHelper does not exist).

- [ ] **Step 3: Implement OwnershipHelper**

Create `src/ForceDelete.Core/OwnershipHelper.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter OwnershipHelperTests`
Expected: PASS (2 tests).

---

## Task 9: RestartManagerLockFinder + ProcessKiller

**Files:**
- Create: `src/ForceDelete.Core/RestartManagerLockFinder.cs`
- Create: `src/ForceDelete.Core/ProcessKiller.cs`
- Create: `tests/ForceDelete.Tests/RestartManagerLockFinderTests.cs`

- [ ] **Step 1: Write the failing test**

If the test process holds a file open, Restart Manager must report the current process id. When nothing holds it, the list is empty. Deterministic, no admin.

Create `tests/ForceDelete.Tests/RestartManagerLockFinderTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter RestartManagerLockFinderTests`
Expected: FAIL (RestartManagerLockFinder does not exist).

- [ ] **Step 3: Implement RestartManagerLockFinder**

Create `src/ForceDelete.Core/RestartManagerLockFinder.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ForceDelete.Core;

/// <summary>Uses the Windows Restart Manager to find processes holding a file open.</summary>
public sealed class RestartManagerLockFinder : ILockFinder
{
    private const int ERROR_MORE_DATA = 234;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2,
        RmService = 3, RmExplorer = 4, RmConsole = 5, RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int flags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle,
        uint nFiles, string[] fileNames,
        uint nApplications, RM_UNIQUE_PROCESS[]? applications,
        uint nServices, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle,
        out uint procInfoNeeded, ref uint procInfo,
        [In, Out] RM_PROCESS_INFO[]? processInfo, ref uint rebootReasons);

    public IReadOnlyList<LockingProcess> FindLockers(string path)
    {
        // Restart Manager expects a normal path, not the \\?\ extended form.
        var key = Guid.NewGuid().ToString();
        if (RmStartSession(out uint session, 0, key) != 0)
            return Array.Empty<LockingProcess>();

        try
        {
            string[] resources = { path };
            if (RmRegisterResources(session, 1, resources, 0, null, 0, null) != 0)
                return Array.Empty<LockingProcess>();

            uint needed = 0, count = 0, reasons = 0;
            int res = RmGetList(session, out needed, ref count, null, ref reasons);
            if (res == 0) return Array.Empty<LockingProcess>();       // nothing holds it
            if (res != ERROR_MORE_DATA) return Array.Empty<LockingProcess>();

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
                return Array.Empty<LockingProcess>();

            var list = new List<LockingProcess>();
            for (int i = 0; i < count; i++)
            {
                int pid = infos[i].Process.dwProcessId;
                string name = infos[i].strAppName ?? string.Empty;
                bool critical = infos[i].ApplicationType == RM_APP_TYPE.RmCritical
                                || CriticalProcessGuard.IsCritical(pid, name);
                list.Add(new LockingProcess(pid, name, critical));
            }
            return list;
        }
        finally
        {
            RmEndSession(session);
        }
    }
}
```

- [ ] **Step 4: Implement ProcessKiller**

Create `src/ForceDelete.Core/ProcessKiller.cs`:

```csharp
using System.Diagnostics;

namespace ForceDelete.Core;

public sealed class ProcessKiller : IProcessKiller
{
    public bool Kill(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return p.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter RestartManagerLockFinderTests`
Expected: PASS (2 tests).

---

## Task 10: DeleteEngine — happy paths (file, folder, junction)

**Files:**
- Create: `src/ForceDelete.Core/DeleteEngine.cs`
- Create: `tests/ForceDelete.Tests/Fakes.cs`
- Create: `tests/ForceDelete.Tests/DeleteEngineHappyPathTests.cs`

- [ ] **Step 1: Create the test fakes**

Create `tests/ForceDelete.Tests/Fakes.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.Tests;

public sealed class RecordingObserver : IDeleteObserver
{
    public List<(string Path, ItemStatus Status)> Statuses { get; } = new();
    public List<(string Message, LogLevel Level)> Logs { get; } = new();
    public void OnStatus(string path, ItemStatus status) => Statuses.Add((path, status));
    public void OnLog(string message, LogLevel level) => Logs.Add((message, level));
    public bool LogContains(string fragment) =>
        Logs.Any(l => l.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

public sealed class FakeLockFinder : ILockFinder
{
    public IReadOnlyList<LockingProcess> Result { get; set; } = Array.Empty<LockingProcess>();
    public string? LastPath { get; private set; }
    public IReadOnlyList<LockingProcess> FindLockers(string path)
    {
        LastPath = path;
        return Result;
    }
}

public sealed class DelegateKiller : IProcessKiller
{
    private readonly Func<int, bool> _kill;
    public List<int> KilledPids { get; } = new();
    public DelegateKiller(Func<int, bool> kill) => _kill = kill;
    public bool Kill(int pid) { KilledPids.Add(pid); return _kill(pid); }
}

public sealed class FakeKillDecision : IKillDecision
{
    public bool Answer { get; set; }
    public bool Asked { get; private set; }
    public bool ShouldKill(string path, IReadOnlyList<LockingProcess> processes)
    {
        Asked = true;
        return Answer;
    }
}

public sealed class NoopKillDecision : IKillDecision
{
    public bool ShouldKill(string path, IReadOnlyList<LockingProcess> processes) => false;
}

public sealed class RealOwnership : IOwnershipHelper
{
    private readonly OwnershipHelper _inner = new();
    public int Calls { get; private set; }
    public void TakeOwnershipAndGrantFullControl(string path)
    {
        Calls++;
        _inner.TakeOwnershipAndGrantFullControl(path);
    }
}

/// <summary>Builds a DeleteEngine with real ownership and no-op kill decision.</summary>
public static class EngineFactory
{
    public static DeleteEngine Simple(RecordingObserver observer) =>
        new DeleteEngine(observer, new FakeLockFinder(), new DelegateKiller(_ => true),
                         new NoopKillDecision(), new RealOwnership());
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/ForceDelete.Tests/DeleteEngineHappyPathTests.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class DeleteEngineHappyPathTests
{
    [Fact]
    public void DeletesPlainFile()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("a.txt");
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(file);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeletesReadOnlyFile()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("ro.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(file);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeletesNestedFolderTree()
    {
        using var ws = new TestWorkspace();
        ws.CreateFile(@"root\a.txt");
        ws.CreateFile(@"root\sub\b.txt");
        ws.CreateFile(@"root\sub\deep\c.txt");
        var root = ws.Path("root");
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(root);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void MissingItem_ReportsDone()
    {
        using var ws = new TestWorkspace();
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(ws.Path("nope.txt"));

        Assert.Equal(ItemStatus.Done, status);
    }

    [Fact]
    public void JunctionInsideTree_IsDeletedButTargetSurvives()
    {
        using var ws = new TestWorkspace();
        // Target lives OUTSIDE the tree we delete.
        var target = ws.CreateDir("outside_target");
        var sentinel = ws.CreateFile(@"outside_target\keep.txt");

        ws.CreateDir("root");
        ws.CreateFile(@"root\a.txt");
        ws.CreateJunction(@"root\link", target);

        var obs = new RecordingObserver();
        var status = EngineFactory.Simple(obs).DeleteItem(ws.Path("root"));

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(Directory.Exists(ws.Path("root")));
        Assert.True(File.Exists(sentinel)); // did NOT recurse through the junction
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter DeleteEngineHappyPathTests`
Expected: FAIL (DeleteEngine does not exist).

- [ ] **Step 4: Implement DeleteEngine**

Create `src/ForceDelete.Core/DeleteEngine.cs`:

```csharp
using System.Threading;

namespace ForceDelete.Core;

/// <summary>
/// Orchestrates force-deletion with escalation. Raw delete calls go through
/// protected virtual seams so tests can script filesystem failures deterministically.
/// </summary>
public class DeleteEngine
{
    private const int MaxDirRetries = 5;

    private readonly IDeleteObserver _observer;
    private readonly ILockFinder _lockFinder;
    private readonly IProcessKiller _killer;
    private readonly IKillDecision _killDecision;
    private readonly IOwnershipHelper _ownership;

    public DeleteEngine(
        IDeleteObserver observer,
        ILockFinder lockFinder,
        IProcessKiller killer,
        IKillDecision killDecision,
        IOwnershipHelper ownership)
    {
        _observer = observer;
        _lockFinder = lockFinder;
        _killer = killer;
        _killDecision = killDecision;
        _ownership = ownership;
    }

    // Seams — overridden in tests to simulate failures.
    protected virtual void RawDeleteFile(string extendedPath) => File.Delete(extendedPath);
    protected virtual void RawDeleteDir(string extendedPath) => Directory.Delete(extendedPath, false);

    /// <summary>Entry point: routes to file or folder handling.</summary>
    public ItemStatus DeleteItem(string path)
    {
        try
        {
            if (Directory.Exists(path)) return DeleteFolder(path);
            if (File.Exists(path)) return DeleteFile(path);

            _observer.OnStatus(path, ItemStatus.Done); // already gone
            return ItemStatus.Done;
        }
        catch (Exception ex)
        {
            return Fail(path, ex.Message);
        }
    }

    private ItemStatus DeleteFile(string path)
    {
        _observer.OnStatus(path, ItemStatus.Deleting);
        var ext = PathUtil.ToExtendedPath(path);

        // 1. plain attempt
        if (TryDeleteFile(path, ext, out var early)) return early!.Value;

        // 2. clear attributes, retry
        try
        {
            AttributeHelper.ClearBlockingAttributes(path);
            _observer.OnLog($"Cleared blocking attributes on {path}", LogLevel.Info);
        }
        catch { /* fall through */ }
        if (TryDeleteFile(path, ext, out var afterAttrs)) return afterAttrs!.Value;

        // 3. take ownership + grant, retry
        _observer.OnStatus(path, ItemStatus.FixingPermissions);
        try
        {
            _ownership.TakeOwnershipAndGrantFullControl(path);
            _observer.OnLog($"Took ownership and granted Full Control on {path}", LogLevel.Action);
        }
        catch (Exception ex)
        {
            return Fail(path, $"Ownership change failed: {ex.Message}");
        }

        try
        {
            RawDeleteFile(ext);
            _observer.OnStatus(path, ItemStatus.Done);
            return ItemStatus.Done;
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(path,
                "Access denied even after taking ownership — likely blocked by Windows " +
                "Controlled Folder Access. Add an exclusion for this app in Windows Security.");
        }
        catch (IOException io) when (IsSharingViolation(io))
        {
            return HandleLocked(path, ext);
        }
        catch (Exception ex)
        {
            return Fail(path, ex.Message);
        }
    }

    /// <summary>
    /// Attempts one delete. Returns true (with a terminal status) if the attempt
    /// resolved the item (deleted, or routed to lock handling). Returns false only
    /// on a plain access-denied, signalling the caller to escalate further.
    /// </summary>
    private bool TryDeleteFile(string path, string ext, out ItemStatus? status)
    {
        try
        {
            RawDeleteFile(ext);
            _observer.OnStatus(path, ItemStatus.Done);
            status = ItemStatus.Done;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            status = null;
            return false; // caller escalates (attributes / ownership)
        }
        catch (IOException io) when (IsSharingViolation(io))
        {
            status = HandleLocked(path, ext);
            return true;
        }
        catch (Exception ex)
        {
            status = Fail(path, ex.Message);
            return true;
        }
    }

    private ItemStatus HandleLocked(string path, string ext)
    {
        _observer.OnStatus(path, ItemStatus.Locked);

        var lockers = _lockFinder.FindLockers(path);
        if (lockers.Count == 0)
        {
            // Transient share — one more try.
            try
            {
                RawDeleteFile(ext);
                _observer.OnStatus(path, ItemStatus.Done);
                return ItemStatus.Done;
            }
            catch (Exception ex)
            {
                return Fail(path, $"File is in use: {ex.Message}");
            }
        }

        var critical = lockers.Where(p => p.IsCritical).ToList();
        if (critical.Count > 0)
        {
            foreach (var p in critical)
                _observer.OnLog(
                    $"{path} is held by protected process {p.Name} (PID {p.Pid}) — cannot safely terminate.",
                    LogLevel.Warning);
            return Fail(path, "Locked by a protected system process.");
        }

        foreach (var p in lockers)
            _observer.OnLog($"{path} is held by {p.Name} (PID {p.Pid}).", LogLevel.Info);

        if (!_killDecision.ShouldKill(path, lockers))
            return Fail(path, "Locked; user declined to close the holding process.");

        foreach (var p in lockers)
        {
            if (_killer.Kill(p.Pid))
                _observer.OnLog($"Closed {p.Name} (PID {p.Pid}).", LogLevel.Action);
            else
                _observer.OnLog($"Failed to close {p.Name} (PID {p.Pid}).", LogLevel.Warning);
        }

        try
        {
            RawDeleteFile(ext);
            _observer.OnStatus(path, ItemStatus.Done);
            return ItemStatus.Done;
        }
        catch (Exception ex)
        {
            return Fail(path, $"Still locked after closing processes: {ex.Message}");
        }
    }

    private ItemStatus DeleteFolder(string path)
    {
        _observer.OnStatus(path, ItemStatus.Deleting);

        if (!Directory.Exists(path))
        {
            _observer.OnStatus(path, ItemStatus.Done);
            return ItemStatus.Done;
        }

        // Reparse point: delete the link, never recurse into it.
        if (ReparsePointHelper.IsReparsePoint(path))
            return DeleteEmptyDir(path, isReparse: true);

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(path);
        }
        catch (UnauthorizedAccessException)
        {
            // Top-down permission repair: gain access before we can enumerate.
            _observer.OnStatus(path, ItemStatus.FixingPermissions);
            try
            {
                _ownership.TakeOwnershipAndGrantFullControl(path);
                _observer.OnLog($"Took ownership of {path} to read its contents.", LogLevel.Action);
                entries = Directory.GetFileSystemEntries(path);
            }
            catch (Exception ex)
            {
                return Fail(path, $"Cannot access folder: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return Fail(path, ex.Message);
        }

        // Bottom-up deletion: children before the parent.
        bool allOk = true;
        foreach (var entry in entries)
        {
            ItemStatus childStatus;
            bool isDir = Directory.Exists(entry);
            if (isDir && ReparsePointHelper.IsReparsePoint(entry))
                childStatus = DeleteEmptyDir(entry, isReparse: true);
            else if (isDir)
                childStatus = DeleteFolder(entry);
            else
                childStatus = DeleteFile(entry);

            if (childStatus != ItemStatus.Done) allOk = false;
        }

        if (!allOk)
        {
            _observer.OnStatus(path, ItemStatus.Failed);
            return ItemStatus.Failed;
        }

        return DeleteEmptyDir(path, isReparse: false);
    }

    private ItemStatus DeleteEmptyDir(string path, bool isReparse)
    {
        var ext = PathUtil.ToExtendedPath(path);

        for (int attempt = 0; attempt < MaxDirRetries; attempt++)
        {
            try
            {
                RawDeleteDir(ext);
                _observer.OnStatus(path, ItemStatus.Done);
                if (isReparse)
                    _observer.OnLog($"Deleted reparse point {path} without following it.", LogLevel.Info);
                return ItemStatus.Done;
            }
            catch (UnauthorizedAccessException)
            {
                try { AttributeHelper.ClearBlockingAttributes(path); } catch { }
                try
                {
                    _ownership.TakeOwnershipAndGrantFullControl(path);
                    _observer.OnLog($"Took ownership of folder {path}.", LogLevel.Action);
                }
                catch { /* retry loop will surface failure */ }
            }
            catch (IOException)
            {
                // "Directory not empty" from Windows' semi-async delete: back off and retry.
                Thread.Sleep(100 * (attempt + 1));
            }
            catch (Exception ex)
            {
                return Fail(path, ex.Message);
            }
        }

        return Fail(path, "Could not delete folder after retries.");
    }

    private ItemStatus Fail(string path, string message)
    {
        _observer.OnLog(message, LogLevel.Error);
        _observer.OnStatus(path, ItemStatus.Failed);
        return ItemStatus.Failed;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code == 32   // ERROR_SHARING_VIOLATION
            || code == 33;  // ERROR_LOCK_VIOLATION
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter DeleteEngineHappyPathTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all tests pass.

---

## Task 11: DeleteEngine — attribute/ownership/CFA escalation via scripted seam

**Files:**
- Create: `tests/ForceDelete.Tests/DeleteEngineEscalationTests.cs`

This task adds no production code — it verifies the escalation ordering through the `RawDeleteFile` seam. If a test reveals a logic bug, fix `DeleteEngine.cs`.

- [ ] **Step 1: Write the scripted-engine tests**

Create `tests/ForceDelete.Tests/DeleteEngineEscalationTests.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.Tests;

/// <summary>
/// A DeleteEngine whose file deletion is scripted: the first (FailCount) RawDeleteFile
/// calls throw UnauthorizedAccessException; subsequent calls succeed (unless AlwaysDeny).
/// Lets us assert the exact escalation sequence without real ACL setup.
/// </summary>
internal sealed class ScriptedEngine : DeleteEngine
{
    private readonly int _failCount;
    private readonly bool _alwaysDeny;
    public int DeleteCalls { get; private set; }

    public ScriptedEngine(
        IDeleteObserver observer, IOwnershipHelper ownership,
        int failCount, bool alwaysDeny = false)
        : base(observer, new FakeLockFinder(), new DelegateKiller(_ => true),
               new NoopKillDecision(), ownership)
    {
        _failCount = failCount;
        _alwaysDeny = alwaysDeny;
    }

    protected override void RawDeleteFile(string extendedPath)
    {
        DeleteCalls++;
        if (_alwaysDeny || DeleteCalls <= _failCount)
            throw new UnauthorizedAccessException();
        // success: no-op (flow test, not a real filesystem effect)
    }

    protected override void RawDeleteDir(string extendedPath) { /* not used here */ }
}

public class DeleteEngineEscalationTests
{
    [Fact]
    public void AccessDenied_TakesOwnership_ThenSucceeds()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("denied.txt");
        var obs = new RecordingObserver();
        var ownership = new RealOwnership();

        // Fail plain + after-attributes (2), succeed on the post-ownership attempt.
        var engine = new ScriptedEngine(obs, ownership, failCount: 2);
        var status = engine.DeleteItem(file);

        Assert.Equal(ItemStatus.Done, status);
        Assert.Equal(1, ownership.Calls);                 // ownership invoked exactly once
        Assert.Equal(3, engine.DeleteCalls);              // plain, post-attr, post-ownership
        Assert.Contains(obs.Statuses, s => s.Status == ItemStatus.FixingPermissions);
        Assert.True(obs.LogContains("Took ownership"));
    }

    [Fact]
    public void AccessDeniedForever_ReportsControlledFolderAccessHint()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("cfa.txt");
        var obs = new RecordingObserver();
        var ownership = new RealOwnership();

        var engine = new ScriptedEngine(obs, ownership, failCount: 0, alwaysDeny: true);
        var status = engine.DeleteItem(file);

        Assert.Equal(ItemStatus.Failed, status);
        Assert.Equal(1, ownership.Calls);
        Assert.True(obs.LogContains("Controlled Folder Access"));
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test --filter DeleteEngineEscalationTests`
Expected: PASS (2 tests). If either fails, the escalation logic in `DeleteEngine.cs` is wrong — fix it, then re-run.

---

## Task 12: DeleteEngine — locked-file branches

**Files:**
- Create: `tests/ForceDelete.Tests/DeleteEngineLockedTests.cs`

Real lock via an open `FileStream`; the injected killer "releases" it by disposing the stream, simulating the holder exiting. No admin, no external process.

- [ ] **Step 1: Write the locked-branch tests**

Create `tests/ForceDelete.Tests/DeleteEngineLockedTests.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.Tests;

public class DeleteEngineLockedTests
{
    private static DeleteEngine Build(
        RecordingObserver obs, ILockFinder finder, IProcessKiller killer, IKillDecision decision)
        => new DeleteEngine(obs, finder, killer, decision, new RealOwnership());

    [Fact]
    public void Locked_UserApproves_KillReleasesLock_Deletes()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("locked.txt");
        var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var obs = new RecordingObserver();
        var finder = new FakeLockFinder
        {
            Result = new[] { new LockingProcess(1234, "holder.exe", false) }
        };
        // "Killing" the holder = releasing the lock.
        var killer = new DelegateKiller(_ => { stream.Dispose(); return true; });
        var decision = new FakeKillDecision { Answer = true };

        var status = Build(obs, finder, killer, decision).DeleteItem(file);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(File.Exists(file));
        Assert.True(decision.Asked);
        Assert.Contains(1234, killer.KilledPids);
    }

    [Fact]
    public void Locked_UserDeclines_MarksFailed_NoKill()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("locked.txt");
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var obs = new RecordingObserver();
        var finder = new FakeLockFinder
        {
            Result = new[] { new LockingProcess(1234, "holder.exe", false) }
        };
        var killer = new DelegateKiller(_ => true);
        var decision = new FakeKillDecision { Answer = false };

        var status = Build(obs, finder, killer, decision).DeleteItem(file);

        Assert.Equal(ItemStatus.Failed, status);
        Assert.True(decision.Asked);
        Assert.Empty(killer.KilledPids);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Locked_ByCriticalProcess_RefusesToKill_MarksFailed()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("locked.txt");
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var obs = new RecordingObserver();
        var finder = new FakeLockFinder
        {
            Result = new[] { new LockingProcess(4, "System", true) }
        };
        var killer = new DelegateKiller(_ => true);
        var decision = new FakeKillDecision { Answer = true };

        var status = Build(obs, finder, killer, decision).DeleteItem(file);

        Assert.Equal(ItemStatus.Failed, status);
        Assert.False(decision.Asked);            // never even offered
        Assert.Empty(killer.KilledPids);
        Assert.True(obs.LogContains("protected process"));
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test --filter DeleteEngineLockedTests`
Expected: PASS (3 tests).

- [ ] **Step 3: Run the full suite**

Run: `dotnet test`
Expected: all tests pass.

---

## Task 13: WinForms App — UI, manifest, wiring

**Files:**
- Create: `src/ForceDelete.App/ForceDelete.App.csproj`
- Create: `src/ForceDelete.App/app.manifest`
- Create: `src/ForceDelete.App/Program.cs`
- Create: `src/ForceDelete.App/MainForm.cs`
- Modify: `ForceDelete.sln` (add the app project)

This task is UI; verification is manual (Task 14). Keep logic thin — the App only implements the seams and marshals updates to the UI thread.

- [ ] **Step 1: Create the app project and add to the solution**

Run:

```bash
dotnet new winforms -n ForceDelete.App -o src/ForceDelete.App -f net9.0-windows
rm src/ForceDelete.App/Form1.cs
rm src/ForceDelete.App/Form1.Designer.cs
dotnet sln add src/ForceDelete.App/ForceDelete.App.csproj
dotnet add src/ForceDelete.App/ForceDelete.App.csproj reference src/ForceDelete.Core/ForceDelete.Core.csproj
```

- [ ] **Step 2: Set the app csproj (manifest + single-file publish)**

Replace `src/ForceDelete.App/ForceDelete.App.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AssemblyName>ForceDelete</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create the manifest (admin + long-path aware)**

Create `src/ForceDelete.App/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="ForceDelete.App"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v2">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 / 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 4: Create the entry point**

Create `src/ForceDelete.App/Program.cs`:

```csharp
using ForceDelete.Core;

namespace ForceDelete.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Enable the privileges the ownership step depends on, once at startup.
        PrivilegeManager.EnableDeletePrivileges();

        Application.Run(new MainForm());
    }
}
```

- [ ] **Step 5: Create the main form**

Create `src/ForceDelete.App/MainForm.cs`:

```csharp
using System.ComponentModel;
using ForceDelete.Core;

namespace ForceDelete.App;

public sealed class MainForm : Form, IDeleteObserver, IKillDecision
{
    private readonly ListView _queue = new();
    private readonly TextBox _log = new();
    private readonly Button _addFiles = new();
    private readonly Button _addFolder = new();
    private readonly Button _remove = new();
    private readonly Button _delete = new();

    // path -> queue row, so status updates find their row.
    private readonly Dictionary<string, ListViewItem> _rows = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "ForceDelete";
        Width = 900;
        Height = 640;
        MinimumSize = new Size(700, 480);

        _addFiles.Text = "Add File(s)…";
        _addFiles.Click += (_, _) => AddFiles();

        _addFolder.Text = "Add Folder…";
        _addFolder.Click += (_, _) => AddFolder();

        _remove.Text = "Remove Selected";
        _remove.Click += (_, _) => RemoveSelected();

        _delete.Text = "Delete All";
        _delete.Click += (_, _) => DeleteAll();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6) };
        top.Controls.AddRange(new Control[] { _addFiles, _addFolder, _remove, _delete });

        _queue.View = View.Details;
        _queue.FullRowSelect = true;
        _queue.Dock = DockStyle.Fill;
        _queue.Columns.Add("Path", 560);
        _queue.Columns.Add("Type", 80);
        _queue.Columns.Add("Status", 160);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.Font = new Font(FontFamily.GenericMonospace, 8.5f);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 360
        };
        split.Panel1.Controls.Add(_queue);
        split.Panel2.Controls.Add(_log);

        Controls.Add(split);
        Controls.Add(top);
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Select file(s) to delete" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            foreach (var f in dlg.FileNames) AddToQueue(f);
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a folder to delete" };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
            AddToQueue(dlg.SelectedPath);
    }

    private void AddToQueue(string path)
    {
        if (_rows.ContainsKey(path)) return;
        var type = Directory.Exists(path) ? "Folder" : "File";
        var row = new ListViewItem(new[] { path, type, ItemStatus.Pending.ToString() });
        _queue.Items.Add(row);
        _rows[path] = row;
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem row in _queue.SelectedItems)
        {
            _rows.Remove(row.Text);
            row.Remove();
        }
    }

    private void DeleteAll()
    {
        var paths = _rows.Keys.ToList();
        if (paths.Count == 0) return;

        var message = "This will PERMANENTLY delete the following. There is no undo.\n\n"
                      + string.Join("\n", paths);
        var confirm = MessageBox.Show(this, message, "Confirm delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        SetButtonsEnabled(false);

        var worker = new BackgroundWorker();
        worker.DoWork += (_, _) =>
        {
            var engine = new DeleteEngine(this, new RestartManagerLockFinder(),
                new ProcessKiller(), this, new OwnershipHelper());
            foreach (var p in paths)
                engine.DeleteItem(p);
        };
        worker.RunWorkerCompleted += (_, _) =>
        {
            SetButtonsEnabled(true);
            Log("Done.", LogLevel.Info);
        };
        worker.RunWorkerAsync();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _addFiles.Enabled = _addFolder.Enabled = _remove.Enabled = _delete.Enabled = enabled;
    }

    // ---- IDeleteObserver (called from the worker thread) ----

    public void OnStatus(string path, ItemStatus status)
    {
        if (InvokeRequired) { BeginInvoke(() => OnStatus(path, status)); return; }
        if (_rows.TryGetValue(path, out var row))
            row.SubItems[2].Text = status.ToString();
    }

    public void OnLog(string message, LogLevel level) => Log(message, level);

    private void Log(string message, LogLevel level)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message, level)); return; }
        _log.AppendText($"[{level}] {message}{Environment.NewLine}");
    }

    // ---- IKillDecision (called from the worker thread; blocks on the UI) ----

    public bool ShouldKill(string path, IReadOnlyList<LockingProcess> processes)
    {
        if (InvokeRequired)
            return (bool)Invoke(() => ShouldKill(path, processes));

        var who = string.Join("\n", processes.Select(p => $"  {p.Name} (PID {p.Pid})"));
        var result = MessageBox.Show(this,
            $"{path}\n\nis locked by:\n{who}\n\nClose these process(es) and delete anyway?",
            "File in use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }
}
```

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build`
Expected: build succeeds for all three projects.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: all Core tests still pass.

---

## Task 14: Publish and manual end-to-end verification

**Files:**
- None (build/publish + manual checks)

- [ ] **Step 1: Publish a single-file executable**

Run:

```bash
dotnet publish src/ForceDelete.App/ForceDelete.App.csproj -c Release -o publish
```

Expected: `publish/ForceDelete.exe` exists.

- [ ] **Step 2: Launch and confirm elevation**

Run (from Explorer or a shell): double-click `publish/ForceDelete.exe`.
Expected: a UAC prompt appears; after consent, the window opens.

- [ ] **Step 3: Manual verification checklist**

Perform each and confirm the observed behavior:

- [ ] **Plain file:** Create `C:\Temp\fd\a.txt`. Add it, Delete All, confirm. Status → Done, file gone.
- [ ] **Read-only file:** Create a file, set it read-only in Explorer. Add, delete. Log shows "Cleared blocking attributes"; file gone.
- [ ] **Nested folder:** Create `C:\Temp\fd\tree` with subfolders/files. Add the folder, delete. Whole tree gone.
- [ ] **Long path:** Create a deeply nested path over 260 chars (e.g. via repeated subfolders). Add the top folder, delete. Succeeds where Explorer would refuse.
- [ ] **Locked file:** Open a file in Notepad, leave it open. Add it, delete. A "File in use" dialog names Notepad; choosing Yes closes Notepad and deletes the file.
- [ ] **Permission-denied folder (the headline case):** As admin, create a folder and remove your own account's permissions (Properties → Security), or pick a folder owned by another account. Add it, delete. Status passes through "Fixing Permissions", log shows "Took ownership and granted Full Control", folder is deleted.
- [ ] **Critical-process safety:** (Do NOT try to delete a live system file.) Confirm by code review that `CriticalProcessGuard` names are refused; optionally point the tool at a file locked by a service and confirm it reports "protected process" and does NOT offer to kill it.

- [ ] **Step 4: Note known limitations (no code change)**

Confirm these are understood and acceptable per the spec:
- Files backing running executables/DLLs mapped by the OS loader, or held by the System process, cannot be deleted while in use — reported as locked/protected.
- Controlled Folder Access, if enabled, blocks deletion in protected user folders even after ownership; the tool reports the CFA hint.
- The exe is unsigned, so SmartScreen shows an "unknown publisher" warning on first run.

---

## Self-Review

**Spec coverage:**
- File/folder pickers as primary selection → Task 13 (Add File(s)/Add Folder). ✅ (drag-drop intentionally omitted per spec.)
- Elevation via manifest → Task 13 Step 3. ✅
- `longPathAware` + selective `\\?\` → Task 3 (PathUtil) + Task 13 manifest. ✅
- Enable SeTakeOwnership/SeRestore privileges → Task 6 + Program.cs (Task 13 Step 4). ✅
- Plain delete → attribute clear → ownership escalation → Tasks 4, 8, 10, 11. ✅
- Controlled Folder Access distinct report → Task 11 (`AccessDeniedForever...` test) + DeleteEngine CFA message. ✅
- Restart Manager lock finding + per-item kill prompt → Tasks 9, 12, 13 (ShouldKill dialog). ✅
- Critical-process guard against BSOD → Tasks 7, 12. ✅
- Reparse-point non-recursion → Tasks 5, 10 (junction test). ✅
- Top-down ACL repair, bottom-up delete → Task 10 (`DeleteFolder`). ✅
- Pending-delete retry → Task 10 (`DeleteEmptyDir` IOException backoff). ✅
- Confirmation dialog listing paths → Task 13 (`DeleteAll`). ✅
- Per-item partial success → Task 10 (`allOk` tracking). ✅
- Background thread + UI marshalling → Task 13 (BackgroundWorker + InvokeRequired). ✅
- Missing-item race → Task 10 (`DeleteItem` already-gone path). ✅

**Placeholder scan:** No TBD/TODO; every code and test step contains complete code. ✅

**Type consistency:** `ItemStatus`, `LogLevel`, `LockingProcess`, and the five interfaces are defined once in Task 2 and used with identical signatures throughout. `TakeOwnershipAndGrantFullControl`, `FindLockers`, `ShouldKill`, `Kill`, `RawDeleteFile`/`RawDeleteDir`, `DeleteItem` names are consistent across Core and tests. ✅

**Known untested-by-unit paths (validated manually in Task 14):** genuine foreign-owned ACL recovery, real long-path deletion, real Restart Manager kill of an external process, and Controlled Folder Access — all require a real elevated/OS environment and are covered by the manual checklist.
