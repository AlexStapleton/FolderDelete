using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ForceDelete.Core;

public enum GuardVerdict { Allow, Warn, Block }

public sealed record GuardResult(GuardVerdict Verdict, string Reason)
{
    public static readonly GuardResult Allowed = new(GuardVerdict.Allow, string.Empty);
}

/// <param name="WarnInside">
/// True: items inside this location are allowed but flagged. False: only the location
/// itself (and anything containing it) is protected — e.g. a user profile, whose
/// contents are ordinary user files.
/// </param>
public sealed record ProtectedLocation(string Path, string Description, bool WarnInside = true);

/// <summary>
/// Decides whether a queued path may be deleted:
/// <list type="bullet">
/// <item>Block — it is a drive root, or it is or CONTAINS a protected location.
/// Not overridable.</item>
/// <item>Warn — it is inside a protected location (e.g. one driver package in the
/// driver store). Allowed after explicit confirmation.</item>
/// <item>Allow — everything else.</item>
/// </list>
/// Paths are resolved to their real location first (8.3 short names, subst drives,
/// junctions in the middle of the path), so an alias can't slip past a text comparison.
/// The final component is NOT followed if it is a link: deleting a link never touches
/// its target, so it must be judged as itself.
/// Checking only queued top-level paths is sufficient because the engine never
/// follows reparse points: a delete can only reach what is physically inside.
/// </summary>
public sealed class PathGuard
{
    private readonly List<(string Path, ProtectedLocation Location)> _locations;
    private readonly Func<string, string> _resolve;

    private static readonly Lazy<PathGuard> Machine = new(() => new PathGuard(MachineLocations()));

    /// <param name="canonicalize">Path resolver; defaults to resolving against the real filesystem.</param>
    public PathGuard(IEnumerable<ProtectedLocation> locations, Func<string, string>? canonicalize = null)
    {
        _resolve = canonicalize ?? Canonicalize;
        _locations = locations.Select(l => (Key(_resolve(l.Path)), l)).ToList();
    }

    /// <summary>The guard for this machine's Windows, Program Files, profile folders, etc.</summary>
    public static PathGuard ForThisMachine() => Machine.Value;

    public GuardResult Check(string path)
    {
        string p;
        try
        {
            p = Key(_resolve(path));
        }
        catch (Exception ex)
        {
            // Fail closed: if we can't tell where it really is, don't delete it.
            return new(GuardVerdict.Block, $"Could not resolve {path} to check it: {ex.Message}");
        }

        var shown = Same(p, Key(path)) ? path : $"{path} (really {p})";

        if (IsRoot(p))
            return new(GuardVerdict.Block,
                $"{shown} is the root of a drive. To wipe a whole drive, format it instead.");

        foreach (var (loc, info) in _locations)
            if (Same(p, loc))
                return new(GuardVerdict.Block, $"{shown} is {info.Description}.");

        // Report the outermost location it contains (the most recognizable one).
        var contained = _locations.Where(l => IsInside(l.Path, p)).OrderBy(l => l.Path.Length).FirstOrDefault();
        if (contained.Location is not null)
            return new(GuardVerdict.Block,
                $"{shown} contains {contained.Location.Description} ({contained.Path}).");

        // Report the innermost location it is inside (the most specific one).
        var container = _locations.Where(l => l.Location.WarnInside && IsInside(p, l.Path))
            .OrderByDescending(l => l.Path.Length).FirstOrDefault();
        if (container.Location is not null)
            return new(GuardVerdict.Warn, $"Inside {container.Location.Description}");

        return GuardResult.Allowed;
    }

    // ---- comparison helpers (paths already resolved) ----

    /// <summary>Comparable form: no \\?\ prefix, backslashes only, no trailing separator.</summary>
    private static string Key(string path) =>
        PathUtil.FromExtendedPath(path).Replace('/', '\\').TrimEnd('\\');

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string child, string parent) =>
        child.Length > parent.Length + 1
        && child[parent.Length] == '\\'
        && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);

    private static bool IsRoot(string key)
    {
        if (key.Length == 2 && key[1] == ':') return true; // "C:"
        // UNC share root: \\server\share
        if (key.StartsWith(@"\\", StringComparison.Ordinal))
            return key.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
        return false;
    }

    // ---- resolving to the real location ----

    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int size, uint flags);

    /// <summary>
    /// The real path: long names, the volume's own drive letter (not a subst alias), and
    /// junctions along the way resolved — but the last component itself not followed.
    /// For a path that doesn't (fully) exist, resolves the deepest existing ancestor and
    /// appends the rest.
    /// </summary>
    public static string Canonicalize(string path)
    {
        var full = PathUtil.FromExtendedPath(Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path));
        var missing = new Stack<string>();
        var current = full;

        while (true)
        {
            var resolved = FinalPath(current);
            if (resolved != null)
            {
                foreach (var segment in missing) resolved = Path.Join(resolved, segment);
                return resolved;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null) return full; // not even the root resolved; compare as given
            missing.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    private static string? FinalPath(string path)
    {
        using var h = CreateFile(PathUtil.ToExtendedPath(path), 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) return null;

        var buffer = new StringBuilder(512);
        int length = GetFinalPathNameByHandle(h, buffer, buffer.Capacity, 0);
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder(length);
            length = GetFinalPathNameByHandle(h, buffer, buffer.Capacity, 0);
        }
        return length > 0 && length <= buffer.Capacity ? PathUtil.FromExtendedPath(buffer.ToString()) : null;
    }

    // ---- this machine's protected locations ----

    private static IEnumerable<ProtectedLocation> MachineLocations()
    {
        var list = new List<ProtectedLocation>();
        void Add(string? path, string description, bool warnInside = true)
        {
            if (!string.IsNullOrWhiteSpace(path))
                list.Add(new ProtectedLocation(path, description, warnInside));
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.SystemDirectory;

        Add(windows, "the Windows folder");
        Add(system, "the Windows system folder (System32)");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "the 32-bit Windows system folder");
        Add(Path.Combine(windows, "WinSxS"), "the Windows component store (WinSxS)");
        Add(Path.Combine(system, "drivers"), "the Windows drivers folder");
        Add(Path.Combine(system, "config"), "the registry hive folder");
        Add(Path.Combine(system, "DriverStore", "FileRepository"), "the driver store");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData");

        // Profiles: protect the folders themselves, not the user files inside them.
        var (profilesDir, profiles) = ProfilesFromRegistry();
        var current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(profilesDir ?? Path.GetDirectoryName(current), "the user profiles folder", warnInside: false);
        foreach (var profile in profiles.Append(current).Where(p => p.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(profile.TrimEnd('\\'));
            Add(profile, $"the user profile of {name}", warnInside: false);
            Add(Path.Combine(profile, "AppData"), $"the AppData folder of {name}", warnInside: false);
        }

        return list;
    }

    private static (string? ProfilesDirectory, List<string> Profiles) ProfilesFromRegistry()
    {
        var profiles = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (key is null) return (null, profiles);

            foreach (var sid in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(sid);
                if (entry?.GetValue("ProfileImagePath") is string image)
                    profiles.Add(Environment.ExpandEnvironmentVariables(image));
            }

            var dir = key.GetValue("ProfilesDirectory") as string;
            return (dir is null ? null : Environment.ExpandEnvironmentVariables(dir), profiles);
        }
        catch
        {
            return (null, profiles); // fall back to the current user's profile only
        }
    }
}
