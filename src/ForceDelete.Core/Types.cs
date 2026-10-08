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
