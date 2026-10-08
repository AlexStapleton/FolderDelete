# FolderDelete

Utility to delete files and folders on Windows — including the ones Explorer refuses to
delete. The app is called **ForceDelete**.

Queue up files and folders, confirm, and ForceDelete works through each one, escalating
only as far as it has to:

1. **Plain delete.**
2. **Clear attributes** — read-only, hidden, system.
3. **Take ownership** and replace the permissions with Full Control (for you,
   Administrators and SYSTEM). Explicit *Deny* entries are removed rather than outvoted.
4. **Find the process holding it** (via the Windows Restart Manager) and, if you agree,
   close that process and retry. This covers files that are open *and* programs or DLLs
   that are currently running.

It also handles:

- **Odd names** that normal tools can't touch: trailing dots or spaces (`name.`),
  reserved device names (`nul`, `con`), and paths longer than 260 characters.
- **Junctions and symlinks** — the link itself is deleted; the tool never follows it into
  the target, and never changes the target's permissions.
- **Partial failure** — every item succeeds or fails on its own. A folder is kept only
  if something inside it could not be removed, and the log says what and why.

## Safety

Deletion is permanent — there is no Recycle Bin and no undo.

- The confirmation dialog lists every path; **Cancel** is the default button.
- **Stop** ends a run after the current item; closing the window mid-run asks first.
- Processes are only closed after you say yes, one prompt per process per run. Only the
  listed process is closed (not its child processes), and only if it is still the same
  process (its PID hasn't been reused).
- It will **never** offer to close critical system processes (`csrss`, `lsass`,
  `wininit`, …), any process Windows marks critical, or Windows **services** (stop those
  in `services.msc` instead).
- There is deliberately **no blocklist of system paths**. If you select `C:\Windows`,
  it will try. Read the confirmation list.
- If an item's ownership/permissions were changed but the item still couldn't be
  deleted, the log says so.

A history of everything deleted is kept in `history.tsv` next to the executable
(**History…** button). It contains your file paths, so don't distribute it.

## Requirements

- Windows 10 or 11, x64.
- Runs as administrator (the manifest requests elevation) — needed to take ownership of
  files belonging to other accounts or the system.
- Files protected by **Controlled Folder Access** (Windows Security → Ransomware
  protection) still can't be deleted until you allow this app there.

## Build

Requires the .NET 9 SDK.

```bash
dotnet test
```

```bash
dotnet publish src/ForceDelete.App -c Release -o publish
```

The publish step produces a single self-contained `ForceDelete.exe` (no .NET install
needed on the target machine).

## Project layout

| Path | Contents |
| --- | --- |
| `src/ForceDelete.Core` | Delete engine, permission repair, lock detection, process handling |
| `src/ForceDelete.App` | Windows Forms UI and deletion history |
| `tests/ForceDelete.Tests` | xUnit tests, including real-filesystem cases (running executables, odd names, deny ACLs, junctions) |
| `docs/` | Original design spec and implementation plan |

## License

GPL-3.0 — see [LICENSE](LICENSE).
