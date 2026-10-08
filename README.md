# FolderDelete

[![Build](https://github.com/AlexStapleton/FolderDelete/actions/workflows/build.yml/badge.svg)](https://github.com/AlexStapleton/FolderDelete/actions/workflows/build.yml)

Utility to delete files and folders on Windows — including the ones Explorer refuses to
delete. The app is called **ForceDelete**.

## Download

Get `ForceDelete.exe` from the [latest release](https://github.com/AlexStapleton/FolderDelete/releases/latest).
It's a single self-contained file; no .NET install is needed.

Development builds of every commit are attached to each
[Build run](https://github.com/AlexStapleton/FolderDelete/actions/workflows/build.yml)
under **Artifacts** (sign-in required).

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
- **Protected locations** are checked when you add something to the queue, and again
  just before it is deleted:

  | Tier | When | Examples | Result |
  | --- | --- | --- | --- |
  | Block | The path is a drive root, or **is or contains** a protected location | `C:\`, `D:\`, `C:\Windows`, `System32`, `WinSxS`, the driver store, `Program Files`, `ProgramData`, `C:\Users`, any user profile or its `AppData` | Refused, with the reason. Cannot be overridden. |
  | Warn | The path is **inside** a protected system location | one package in `DriverStore\FileRepository`, a folder in `Program Files` | Highlighted in the queue; the confirmation needs an extra tick-box |
  | Allow | Anything else, including ordinary files inside your profile | `Downloads\…`, `.nuget\packages\…` | Deleted as normal |

  Paths are resolved to where they really are first, so `C:\PROGRA~1`, a `subst` drive
  letter, or a junction partway along the path can't slip past. A queued junction or
  symlink is judged as itself, since deleting it never touches its target.
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

GitHub Actions runs the tests and builds the exe on every push and pull request
([`.github/workflows/build.yml`](.github/workflows/build.yml)).

### Releasing

Tag a commit with a version and push the tag; the workflow builds, stamps the version
into the exe and publishes a GitHub Release with `ForceDelete.exe` attached:

```bash
git tag v1.0.0
```

```bash
git push origin v1.0.0
```

## Project layout

| Path | Contents |
| --- | --- |
| `src/ForceDelete.Core` | Delete engine, permission repair, lock detection, process handling |
| `src/ForceDelete.App` | Windows Forms UI and deletion history |
| `tests/ForceDelete.Tests` | xUnit tests, including real-filesystem cases (running executables, odd names, deny ACLs, junctions) |
| `docs/` | Original design spec and implementation plan |

## License

GPL-3.0 — see [LICENSE](LICENSE).
