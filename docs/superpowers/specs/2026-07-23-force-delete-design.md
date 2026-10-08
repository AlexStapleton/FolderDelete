# ForceDelete — Design Spec

Date: 2026-07-23 (updated 2026-07-24)

## Purpose

A Windows 11 desktop tool that deletes any file or folder the user points it
at, including ones a normal Explorer delete would refuse: permission-denied
items, items locked by a running process, and items with paths longer than
260 characters. The user selects items via a file/folder browser; the tool
escalates automatically through the obstacles above and shows what it did.

## Tech stack

- C# / .NET 9, WinForms, target framework `net9.0-windows` (required — the
  file-security / ownership APIs only exist on the Windows-targeted
  framework).
- Single self-contained executable (no separate runtime install required).
- Must run elevated (admin). The app self-elevates via a UAC prompt on
  launch (manifest `requestedExecutionLevel = requireAdministrator`), because
  taking ownership of files requires admin rights.
- App manifest also declares `longPathAware = true`; the tool additionally
  applies the `\\?\` extended-length prefix selectively for the specific
  cases that need it (see Long paths below).

## UI

- Main window with:
  - Two selection buttons: **"Add File(s)…"** (multi-select
    `OpenFileDialog`) and **"Add Folder…"** (folder browser). These are the
    primary and only selection mechanism. (Drag-and-drop is intentionally
    NOT used: an elevated/high-integrity process cannot receive drag-drop
    from Explorer due to Windows UIPI message filtering, so it would appear
    silently broken.)
  - A queue list of added items, each showing: path, type (file/folder),
    and status (Pending / Deleting / Fixing Permissions / Locked / Done /
    Failed).
  - A "Remove selected" button to take items back out of the queue before
    deleting.
  - A "Delete All" button that processes the queue.
  - A log panel showing a running, timestamped account of actions taken per
    item (e.g. "Took ownership of C:\path — was denied by ACL", "Closed
    handle held by chrome.exe (PID 4821)", "Deleted").
- Before deletion starts, a single confirmation dialog lists every top-level
  path queued for deletion (full paths, scrollable) and requires an explicit
  "Yes, delete these" click. No path-blocklist guardrail beyond this
  confirmation — the user explicitly chose no restricted-path list.

## Delete pipeline

Processing runs on a background thread so the UI stays responsive; status and
log updates are marshalled back to the UI thread.

### Privilege setup (once, at start of a delete run)

Before any delete work, enable `SeTakeOwnershipPrivilege` and
`SeRestorePrivilege` in the process token via `AdjustTokenPrivileges`
(P/Invoke). An elevated admin token *contains* these privileges but leaves
them **disabled by default**; `SetOwner` fails with Access Denied until they
are enabled. This is the single most important step for the permission
feature to work at all.

### Per file (leaf item)

All comparisons of "access denied" vs "in use" are based on the specific
Win32 error / exception type, not a generic catch.

1. **Plain delete attempt.** Try to delete the file. Most items succeed here.
2. **Attribute block (ReadOnly/Hidden/System).** If the failure is due to
   file attributes, clear ReadOnly/Hidden/System and retry. Logged at a
   low-noise "info" level.
3. **Access denied (ACL).** If the failure is an access-denied error:
   - Mark the item "Fixing Permissions" and log it.
   - Take ownership (`SetOwner` to the current user) and grant the current
     user Full Control (`FileSystemAccessRule`), then retry the delete.
   - Log exactly what was done ("Took ownership", "Granted Full Control to
     <user>", then the retry result).
   - Distinguish **Controlled Folder Access**: if the item lives under a
     Defender-protected folder and the delete is still blocked after
     ownership succeeds, report it distinctly ("blocked by Windows
     Controlled Folder Access — add an exclusion for this app in Windows
     Security"), because ownership cannot resolve that block.
   - If it still fails, mark "Failed" with the underlying error.
4. **Sharing violation (locked by a process).** If the failure is a
   sharing/in-use error:
   - Use the Windows Restart Manager API (`RmStartSession`,
     `RmRegisterResources`, `RmGetList`) to identify the process(es) holding
     the file open.
   - Show process name(s)/PID(s) and prompt (per item) whether to
     close/kill them.
   - **Critical-process guard:** if a reported holder is a known-critical or
     unkillable process (System PID 4, csrss.exe, wininit.exe, smss.exe,
     services.exe, lsass.exe, or a Windows service host), do NOT offer to
     kill it — killing these can blue-screen the machine. Log that the file
     is held by a protected process and mark the item "Failed" (locked).
   - Otherwise, on user confirmation, terminate the process(es) and retry.
   - If the user declines, or the retry still fails, mark "Failed".

### Per folder

Folders are NOT deleted with .NET's built-in recursive delete
(`Directory.Delete(path, true)`): its recursive form aborts the entire
subtree on the first locked/denied child, which is incompatible with the
per-item "some succeed, some fail" model. A custom walker is used instead:

1. **Reparse-point check first.** If the folder has
   `FILE_ATTRIBUTE_REPARSE_POINT` (junction, symlink, mount point, or a
   OneDrive-style placeholder), delete the **link itself** and do NOT recurse
   into it. Following it could delete data outside the intended tree (e.g. a
   junction pointing at C:\Users). This check happens before any enumeration.
2. **Top-down permission repair.** Attempt to enumerate the folder's
   children. If enumeration fails with access-denied, take ownership + grant
   Full Control on the folder first (you cannot list a folder's contents
   until you have access to it), then enumerate. Ownership/ACL repair
   therefore flows **top-down**.
3. **Recurse into children** (subfolders via this same folder routine, files
   via the per-file routine).
4. **Delete the now-empty folder last.** Actual deletion flows **bottom-up**:
   children before parent. After clearing children, delete the folder itself
   (clearing its own attributes / taking ownership if needed, same
   escalation as a file).
5. **Pending-delete retry.** Windows directory removal is semi-asynchronous;
   a parent delete immediately after its children can transiently fail with
   "directory not empty." Retry the folder delete a few times with a short
   backoff before marking it "Failed".

A folder can partially succeed (e.g. 9 of 10 children deleted, 1 failed); the
queue and log reflect per-item outcomes, and the parent folder is left in
place if any child survived.

## Long paths

- The `\\?\` extended-length prefix is applied selectively, not blanket:
  specifically for paths at/over the legacy 260-char limit and for names that
  normalization would otherwise mangle (trailing dot/space, reserved device
  names). It is not applied to every call, because the prefix disables path
  normalization and some APIs in the chain (parts of System.IO, the
  AccessControl APIs, and Restart Manager's `RmRegisterResources`) do not
  accept or misbehave with it. Where the prefix is used it must be a fully
  qualified path with backslashes only.
- The manifest `longPathAware` opt-in covers the common case without the
  prefix on Windows 11 with long paths enabled.

## Error handling / edge cases

- Item no longer exists when processing starts (race): mark "Done" (already
  gone), no error.
- Drive root or OS volume path: no special blocking (per guardrail decision);
  underlying Windows errors surface as "Failed" with the error message.
- Restart Manager reports a process that exits on its own before the kill:
  treat as success, retry the delete.
- User cancels the confirmation dialog: nothing is touched.
- Access-denied that persists after successful ownership change: reported as
  a distinct outcome (likely Controlled Folder Access or a protected
  process), not silently retried forever.

## Out of scope

- No scheduling/recurring deletion.
- No "recycle bin" / undo — deletions are permanent, consistent with the
  tool's purpose.
- No blocklist of protected system paths (explicitly declined). Note the
  critical-*process* guard above is a stability guard against BSODs, not a
  path guard.
- No Explorer context-menu integration (declined in favor of GUI-only).
- No drag-and-drop (declined; blocked by UIPI for elevated processes).
