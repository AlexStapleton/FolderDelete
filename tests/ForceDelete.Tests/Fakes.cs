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
    public bool Kill(LockingProcess process) { KilledPids.Add(process.Pid); return _kill(process.Pid); }
}

public sealed class FakeKillDecision : IKillDecision
{
    public bool Answer { get; set; }
    public int AskCount { get; private set; }
    public bool Asked => AskCount > 0;
    public bool ShouldKill(string path, IReadOnlyList<LockingProcess> processes)
    {
        AskCount++;
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
