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
