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

    [Fact]
    public void DeclinedProcess_IsNotAskedAgainInSameRun()
    {
        using var ws = new TestWorkspace();
        var a = ws.CreateFile(@"dir\a.txt");
        var b = ws.CreateFile(@"dir\b.txt");
        using var sa = File.Open(a, FileMode.Open, FileAccess.Read, FileShare.None);
        using var sb = File.Open(b, FileMode.Open, FileAccess.Read, FileShare.None);

        var obs = new RecordingObserver();
        var finder = new FakeLockFinder { Result = new[] { new LockingProcess(1234, "holder", false) } };
        var decision = new FakeKillDecision { Answer = false };

        var status = Build(obs, finder, new DelegateKiller(_ => true), decision).DeleteItem(ws.Path("dir"));

        Assert.Equal(ItemStatus.Failed, status);
        Assert.Equal(1, decision.AskCount);
    }

    [Fact]
    public void Locked_ByService_RefusesToKill_AndNamesTheService()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("locked.txt");
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);

        var obs = new RecordingObserver();
        var finder = new FakeLockFinder
        {
            Result = new[] { new LockingProcess(2222, "Host Process for Windows Services", true, ServiceName: "Spooler") }
        };
        var killer = new DelegateKiller(_ => true);
        var decision = new FakeKillDecision { Answer = true };

        var status = Build(obs, finder, killer, decision).DeleteItem(file);

        Assert.Equal(ItemStatus.Failed, status);
        Assert.False(decision.Asked);
        Assert.Empty(killer.KilledPids);
        Assert.True(obs.LogContains("Spooler"));
    }
}
