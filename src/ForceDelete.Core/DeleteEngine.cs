using System.Threading;

namespace ForceDelete.Core;

/// <summary>
/// Orchestrates force-deletion with escalation. Raw delete calls go through
/// protected virtual seams so tests can script filesystem failures deterministically.
/// </summary>
public class DeleteEngine
{
    private const int MaxDirRetries = 5;

    // Guards against StackOverflowException (uncatchable, crashes the process) on
    // pathologically deep trees — which the long-path support otherwise enables.
    // Legitimate folder trees are nowhere near this deep.
    private const int MaxRecursionDepth = 1000;

    private const string ControlledFolderAccessHint =
        "Access denied even after taking ownership — likely blocked by Windows " +
        "Controlled Folder Access. Add an exclusion for this app in Windows Security.";

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
            return Fail(path, ControlledFolderAccessHint);
        }
        catch (IOException io) when (IsSharingViolation(io))
        {
            return HandleLocked(path, () => RawDeleteFile(ext));
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
            status = HandleLocked(path, () => RawDeleteFile(ext));
            return true;
        }
        catch (Exception ex)
        {
            status = Fail(path, ex.Message);
            return true;
        }
    }

    private ItemStatus HandleLocked(string path, Action rawDelete)
    {
        _observer.OnStatus(path, ItemStatus.Locked);

        var lockers = _lockFinder.FindLockers(path);
        if (lockers.Count == 0)
        {
            // Transient share — one more try.
            try
            {
                rawDelete();
                _observer.OnStatus(path, ItemStatus.Done);
                return ItemStatus.Done;
            }
            catch (Exception ex)
            {
                return Fail(path, $"In use: {ex.Message}");
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
            rawDelete();
            _observer.OnStatus(path, ItemStatus.Done);
            return ItemStatus.Done;
        }
        catch (Exception ex)
        {
            return Fail(path, $"Still locked after closing processes: {ex.Message}");
        }
    }

    private ItemStatus DeleteFolder(string path, int depth = 0)
    {
        _observer.OnStatus(path, ItemStatus.Deleting);

        if (depth > MaxRecursionDepth)
            return Fail(path,
                $"Folder nesting exceeds the safe recursion depth of {MaxRecursionDepth} — aborting to avoid a crash.");

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
                childStatus = DeleteFolder(entry, depth + 1);
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
        int denialStage = 0; // 0=none, 1=attributes cleared, 2=ownership taken

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
                // Same staged escalation as a file: attributes, then ownership, then CFA hint.
                if (denialStage == 0)
                {
                    try
                    {
                        AttributeHelper.ClearBlockingAttributes(path);
                        _observer.OnLog($"Cleared blocking attributes on {path}", LogLevel.Info);
                    }
                    catch { /* fall through to retry */ }
                    denialStage = 1;
                }
                else if (denialStage == 1)
                {
                    _observer.OnStatus(path, ItemStatus.FixingPermissions);
                    try
                    {
                        _ownership.TakeOwnershipAndGrantFullControl(path);
                        _observer.OnLog($"Took ownership and granted Full Control on folder {path}", LogLevel.Action);
                    }
                    catch (Exception ex)
                    {
                        return Fail(path, $"Ownership change failed: {ex.Message}");
                    }
                    denialStage = 2;
                }
                else
                {
                    return Fail(path, ControlledFolderAccessHint);
                }
            }
            catch (IOException io) when (IsSharingViolation(io))
            {
                // Folder held open by a process (e.g. it is a process's current directory).
                return HandleLocked(path, () => RawDeleteDir(ext));
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
