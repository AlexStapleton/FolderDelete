using System.Threading;

namespace ForceDelete.Core;

/// <summary>
/// Orchestrates force-deletion with escalation. Raw delete calls go through
/// protected virtual seams so tests can script filesystem failures deterministically.
///
/// Every filesystem call uses the \\?\ form of the path (see PathUtil) so names that
/// Win32 would normalize away — "name.", "name ", "nul" — are hit exactly.
/// One engine instance is one run: it remembers which processes the user declined to close.
/// </summary>
public class DeleteEngine
{
    private const int MaxNotEmptyRetries = 5;

    // After holders are closed, the OS can take a moment to release handles / unmap images.
    private const int MaxReleaseRetries = 5;

    // Guards against StackOverflowException (uncatchable, crashes the process) on
    // pathologically deep trees — which the long-path support otherwise enables.
    // Legitimate folder trees are nowhere near this deep.
    private const int MaxRecursionDepth = 1000;

    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_DIR_NOT_EMPTY = 145;

    private const string ControlledFolderAccessHint =
        "Access denied even after taking ownership, and no process is holding it — likely blocked " +
        "by Windows Controlled Folder Access or another security product. Add an exclusion for " +
        "this app in Windows Security.";

    private const string FolderInUseHint =
        "Folder is in use by a process (for example as its working directory, or open in Explorer " +
        "or a terminal). Windows cannot report which process holds a folder — close windows and " +
        "terminals inside it, then retry.";

    private readonly IDeleteObserver _observer;
    private readonly ILockFinder _lockFinder;
    private readonly IProcessKiller _killer;
    private readonly IKillDecision _killDecision;
    private readonly IOwnershipHelper _ownership;
    private readonly CancellationToken _cancel;
    private readonly PathGuard _guard;

    // Processes the user refused to close this run — don't ask again for every file they hold.
    private readonly HashSet<(int Pid, DateTime? Start)> _declined = new();

    // Items whose owner/DACL we rewrote, so a later failure can say they were left modified.
    private readonly HashSet<string> _permissionsChanged = new(StringComparer.OrdinalIgnoreCase);

    public DeleteEngine(
        IDeleteObserver observer,
        ILockFinder lockFinder,
        IProcessKiller killer,
        IKillDecision killDecision,
        IOwnershipHelper ownership,
        CancellationToken cancellation = default,
        PathGuard? guard = null)
    {
        _observer = observer;
        _lockFinder = lockFinder;
        _killer = killer;
        _killDecision = killDecision;
        _ownership = ownership;
        _cancel = cancellation;
        _guard = guard ?? PathGuard.ForThisMachine();
    }

    // Seams — overridden in tests to simulate failures.
    protected virtual void RawDeleteFile(string extendedPath) => File.Delete(extendedPath);
    protected virtual void RawDeleteDir(string extendedPath) => Directory.Delete(extendedPath, false);

    /// <summary>Entry point: routes to file or folder handling.</summary>
    public ItemStatus DeleteItem(string path)
    {
        try
        {
            if (_cancel.IsCancellationRequested) return Cancel(path);

            // Second line of defence behind the UI: holds for any caller of the engine.
            var guard = _guard.Check(path);
            if (guard.Verdict == GuardVerdict.Block)
                return Fail(path, $"Refused — protected location. {guard.Reason}");

            var attrs = GetAttributesOrRepair(path);
            if (attrs is null)
            {
                _observer.OnLog($"{path} no longer exists — nothing to delete.", LogLevel.Info);
                _observer.OnStatus(path, ItemStatus.Done);
                return ItemStatus.Done;
            }

            return attrs.Value.HasFlag(FileAttributes.Directory)
                ? DeleteFolder(path, attrs.Value, depth: 0)
                : DeleteFile(path);
        }
        catch (Exception ex)
        {
            return Fail(path, ex.Message);
        }
    }

    /// <summary>
    /// The item's attributes, or null if it does not exist. (File.Exists/Directory.Exists
    /// can't be used: they also return false for an item we merely cannot read.)
    /// </summary>
    private FileAttributes? GetAttributesOrRepair(string path)
    {
        var ext = PathUtil.ToExtendedPath(path);
        try
        {
            return File.GetAttributes(ext);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, but we can't even read its attributes: repair, then look again.
            _observer.OnStatus(path, ItemStatus.FixingPermissions);
            TakeOwnership(path, " to read its attributes");
            return File.GetAttributes(ext);
        }
    }

    private ItemStatus DeleteFile(string path)
    {
        _observer.OnStatus(path, ItemStatus.Deleting);
        var ext = PathUtil.ToExtendedPath(path);
        return Remove(path, () => RawDeleteFile(ext), isDir: false, isReparse: false);
    }

    private ItemStatus DeleteFolder(string path, FileAttributes attrs, int depth)
    {
        _observer.OnStatus(path, ItemStatus.Deleting);

        if (depth > MaxRecursionDepth)
            return Fail(path,
                $"Folder nesting exceeds the safe recursion depth of {MaxRecursionDepth} — aborting to avoid a crash.");

        var ext = PathUtil.ToExtendedPath(path);

        // Reparse point: delete the link, never recurse into it.
        if (attrs.HasFlag(FileAttributes.ReparsePoint))
            return Remove(path, () => RawDeleteDir(ext), isDir: true, isReparse: true);

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(ext).GetFileSystemInfos();
        }
        catch (DirectoryNotFoundException)
        {
            return Succeed(path, isReparse: false); // vanished underneath us
        }
        catch (UnauthorizedAccessException)
        {
            // Top-down permission repair: gain access before we can enumerate.
            _observer.OnStatus(path, ItemStatus.FixingPermissions);
            try
            {
                TakeOwnership(path, " to read its contents");
                entries = new DirectoryInfo(ext).GetFileSystemInfos();
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
            if (_cancel.IsCancellationRequested) return Cancel(path);

            // Type comes from the directory listing itself: no extra access is needed, and
            // names Win32 would mangle are classified correctly.
            var child = PathUtil.FromExtendedPath(entry.FullName);
            var childStatus = entry.Attributes.HasFlag(FileAttributes.Directory)
                ? DeleteFolder(child, entry.Attributes, depth + 1)
                : DeleteFile(child);

            if (childStatus == ItemStatus.Cancelled) return Cancel(path);
            if (childStatus != ItemStatus.Done) allOk = false;
        }

        if (!allOk)
            return Fail(path, "Folder kept because some of its contents could not be deleted.");

        return Remove(path, () => RawDeleteDir(ext), isDir: true, isReparse: false);
    }

    /// <summary>
    /// Deletes one file or empty folder, escalating on access-denied: clear attributes, then
    /// take ownership, then look for a holding process. A running .exe/.dll (or an item already
    /// pending deletion) fails with access-denied rather than a sharing violation, so the lock
    /// check must come after permissions have been ruled out — not only on sharing violations.
    /// </summary>
    private ItemStatus Remove(string path, Action rawDelete, bool isDir, bool isReparse)
    {
        int notEmptyRetries = 0;
        bool attributesCleared = false, ownershipTaken = false;

        while (true)
        {
            var outcome = TryRawDelete(rawDelete, out var error);
            switch (outcome)
            {
                case Outcome.Deleted:
                    return Succeed(path, isReparse);

                case Outcome.InUse:
                    return HandleLocked(path, rawDelete, _lockFinder.FindLockers(path), isDir);

                case Outcome.NotEmpty when notEmptyRetries < MaxNotEmptyRetries:
                    // Windows' removal of the children is semi-asynchronous: back off and retry.
                    Thread.Sleep(100 * ++notEmptyRetries);
                    continue;

                case Outcome.Denied when !attributesCleared:
                    attributesCleared = true;
                    TryClearAttributes(path);
                    continue;

                case Outcome.Denied when !ownershipTaken:
                    ownershipTaken = true;
                    _observer.OnStatus(path, ItemStatus.FixingPermissions);
                    try
                    {
                        TakeOwnership(path, "");
                    }
                    catch (Exception ex)
                    {
                        return Fail(path, $"Ownership change failed: {ex.Message}");
                    }
                    continue;

                case Outcome.Denied:
                    var lockers = _lockFinder.FindLockers(path);
                    return lockers.Count > 0
                        ? HandleLocked(path, rawDelete, lockers, isDir)
                        : Fail(path, ControlledFolderAccessHint);

                default:
                    return Fail(path, error?.Message ?? "Unknown error.");
            }
        }
    }

    private ItemStatus HandleLocked(string path, Action rawDelete, IReadOnlyList<LockingProcess> lockers, bool isDir)
    {
        _observer.OnStatus(path, ItemStatus.Locked);

        if (lockers.Count == 0)
        {
            // Nothing reported: a transient share, or a folder (Restart Manager only tracks files).
            if (RetryAfterRelease(rawDelete, out var transientError)) return Succeed(path, isReparse: false);
            return Fail(path, isDir ? FolderInUseHint : $"In use: {transientError?.Message}");
        }

        var critical = lockers.Where(p => p.IsCritical).ToList();
        if (critical.Count > 0)
        {
            foreach (var p in critical)
                _observer.OnLog(p.ServiceName is { } service
                        ? $"{path} is held by the Windows service '{service}' ({p.Name}, PID {p.Pid}). " +
                          "Services are not terminated — stop it in services.msc, then retry."
                        : $"{path} is held by protected process {p.Name} (PID {p.Pid}) — cannot safely terminate.",
                    LogLevel.Warning);
            return Fail(path, critical.Any(p => p.ServiceName != null)
                ? "Locked by a Windows service."
                : "Locked by a protected system process.");
        }

        foreach (var p in lockers)
            _observer.OnLog($"{path} is held by {p.Name} (PID {p.Pid}).", LogLevel.Info);

        if (lockers.All(p => _declined.Contains((p.Pid, p.StartTimeUtc))))
            return Fail(path, $"Locked by {string.Join(", ", lockers.Select(p => p.Name))}; " +
                              "you chose not to close it earlier in this run.");

        if (!_killDecision.ShouldKill(path, lockers))
        {
            foreach (var p in lockers) _declined.Add((p.Pid, p.StartTimeUtc));
            return Fail(path, "Locked; user declined to close the holding process.");
        }

        foreach (var p in lockers)
        {
            if (_killer.Kill(p))
                _observer.OnLog($"Closed {p.Name} (PID {p.Pid}).", LogLevel.Action);
            else
                _observer.OnLog($"Failed to close {p.Name} (PID {p.Pid}) — it may be protected, " +
                                "or that PID now belongs to a different process.", LogLevel.Warning);
        }

        if (RetryAfterRelease(rawDelete, out var error)) return Succeed(path, isReparse: false);
        return Fail(path, $"Still locked after closing processes: {error?.Message}");
    }

    private bool RetryAfterRelease(Action rawDelete, out Exception? error)
    {
        for (int attempt = 1; ; attempt++)
        {
            var outcome = TryRawDelete(rawDelete, out error);
            if (outcome == Outcome.Deleted) return true;
            if (attempt >= MaxReleaseRetries || outcome is not (Outcome.InUse or Outcome.Denied)) return false;
            Thread.Sleep(100 * attempt);
        }
    }

    private void TryClearAttributes(string path)
    {
        try
        {
            if (AttributeHelper.ClearBlockingAttributes(path))
                _observer.OnLog($"Cleared blocking attributes on {path}", LogLevel.Info);
        }
        catch { /* fall through to the next escalation step */ }
    }

    private void TakeOwnership(string path, string purpose)
    {
        _ownership.TakeOwnershipAndGrantFullControl(path);
        _permissionsChanged.Add(path);
        _observer.OnLog($"Took ownership and granted Full Control on {path}{purpose}.", LogLevel.Action);
    }

    private ItemStatus Succeed(string path, bool isReparse)
    {
        _observer.OnStatus(path, ItemStatus.Done);
        if (isReparse)
            _observer.OnLog($"Deleted reparse point {path} without following it.", LogLevel.Info);
        return ItemStatus.Done;
    }

    private ItemStatus Fail(string path, string message)
    {
        _observer.OnLog(message.Contains(path, StringComparison.OrdinalIgnoreCase) ? message : $"{path}: {message}",
            LogLevel.Error);
        if (_permissionsChanged.Contains(path))
            _observer.OnLog($"Note: the owner and permissions of {path} were changed, but it was not deleted.",
                LogLevel.Warning);
        _observer.OnStatus(path, ItemStatus.Failed);
        return ItemStatus.Failed;
    }

    private ItemStatus Cancel(string path)
    {
        _observer.OnLog($"Cancelled: {path}", LogLevel.Warning);
        _observer.OnStatus(path, ItemStatus.Cancelled);
        return ItemStatus.Cancelled;
    }

    private enum Outcome { Deleted, Denied, InUse, NotEmpty, Error }

    private static Outcome TryRawDelete(Action rawDelete, out Exception? error)
    {
        try
        {
            rawDelete();
            error = null;
            return Outcome.Deleted;
        }
        catch (Exception ex)
        {
            error = ex;
            return ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => Outcome.Deleted, // already gone
                UnauthorizedAccessException => Outcome.Denied,
                IOException io when Win32Code(io) is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => Outcome.InUse,
                IOException io when Win32Code(io) == ERROR_DIR_NOT_EMPTY => Outcome.NotEmpty,
                _ => Outcome.Error
            };
        }
    }

    /// <summary>The Win32 error code inside an HRESULT of the form 0x8007xxxx, else -1.</summary>
    private static int Win32Code(IOException ex) =>
        (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : -1;
}
