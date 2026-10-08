using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ForceDelete.Core;

namespace ForceDelete.Tests;

/// <summary>
/// End-to-end cases against the real filesystem — the failure modes fakes can't model.
/// </summary>
public class DeleteEngineRealFileSystemTests
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private static string Raw(string path) => @"\\?\" + path;

    [Fact]
    public void FolderWithTrailingDotSpaceAndReservedNames_IsFullyDeleted()
    {
        using var ws = new TestWorkspace();
        var root = ws.CreateDir("root");
        // Only creatable through the \\?\ prefix; normal Win32 paths silently rename these.
        File.WriteAllText(Raw(Path.Combine(root, "bad.")), "x");
        File.WriteAllText(Raw(Path.Combine(root, "trail ")), "x");
        File.WriteAllText(Raw(Path.Combine(root, "nul")), "x");
        Directory.CreateDirectory(Raw(Path.Combine(root, "dirdot.")));
        File.WriteAllText(Raw(Path.Combine(root, @"dirdot.\inner.txt")), "x");

        var obs = new RecordingObserver();
        var status = EngineFactory.Simple(obs).DeleteItem(root);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(Directory.Exists(Raw(root)));
    }

    [Fact]
    public void TopLevelTrailingDotFile_IsActuallyDeleted()
    {
        using var ws = new TestWorkspace();
        var file = Path.Combine(ws.Root, "bad.");
        File.WriteAllText(Raw(file), "x");

        var status = EngineFactory.Simple(new RecordingObserver()).DeleteItem(file);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(File.Exists(Raw(file)));
    }

    [Fact]
    public void RunningExecutable_IsTreatedAsLocked_AndDeletedAfterKill()
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
            Thread.Sleep(300); // let the image load

            var obs = new RecordingObserver();
            var decision = new FakeKillDecision { Answer = true };
            var engine = new DeleteEngine(obs, new RestartManagerLockFinder(),
                new ProcessKiller(), decision, new RealOwnership());

            var status = engine.DeleteItem(exe);

            Assert.True(decision.Asked, "running image should be offered for closing");
            Assert.Equal(ItemStatus.Done, status);
            Assert.False(File.Exists(exe));
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            if (!proc.HasExited) { proc.Kill(); proc.WaitForExit(5000); }
        }
    }

    [Fact]
    public void ExplicitDenyAces_AreOverriddenByPermissionRepair()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("denied");
        var file = ws.CreateFile(@"denied\f.txt");

        var fileAcl = new FileInfo(file).GetAccessControl();
        fileAcl.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Delete, AccessControlType.Deny));
        new FileInfo(file).SetAccessControl(fileAcl);

        var dirAcl = new DirectoryInfo(dir).GetAccessControl();
        dirAcl.AddAccessRule(new FileSystemAccessRule(Everyone,
            FileSystemRights.ListDirectory | FileSystemRights.DeleteSubdirectoriesAndFiles,
            AccessControlType.Deny));
        new DirectoryInfo(dir).SetAccessControl(dirAcl);

        var obs = new RecordingObserver();
        var status = EngineFactory.Simple(obs).DeleteItem(dir);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void UnreadableItem_IsNotReportedAsAlreadyGone()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("hidden");
        var file = ws.CreateFile(@"hidden\secret.txt");

        // Item can't be stat'ed: no ReadAttributes on it, no ListDirectory on its parent.
        // File.Exists/Directory.Exists both return false for it, though it is there.
        var fileAcl = new FileInfo(file).GetAccessControl();
        fileAcl.AddAccessRule(new FileSystemAccessRule(Everyone,
            FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes, AccessControlType.Deny));
        new FileInfo(file).SetAccessControl(fileAcl);
        var dirAcl = new DirectoryInfo(dir).GetAccessControl();
        dirAcl.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.ListDirectory, AccessControlType.Deny));
        new DirectoryInfo(dir).SetAccessControl(dirAcl);
        // Unelevated, File.Exists(file) is now false — the old "already gone" trap. (Not
        // asserted: elevated with SeBackupPrivilege enabled, as on CI, .NET can still read it.)

        var obs = new RecordingObserver();
        var status = EngineFactory.Simple(obs).DeleteItem(file);

        // Reopen the parent so we can see the truth.
        new OwnershipHelper().TakeOwnershipAndGrantFullControl(dir);
        bool stillThere = File.Exists(file)
            || new DirectoryInfo(dir).EnumerateFiles().Any();

        // Either it was really deleted, or it was reported as failed — never a false "Done".
        Assert.False(status == ItemStatus.Done && stillThere);
        Assert.True(status == ItemStatus.Done, string.Join("\n", obs.Logs.Select(l => l.Message)));
        Assert.False(stillThere);
    }
}
