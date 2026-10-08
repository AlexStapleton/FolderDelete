namespace ForceDelete.Core;

/// <summary>
/// Identifies processes that must never be terminated: killing them blue-screens
/// Windows or is impossible (the System process). Used to refuse the kill offer.
/// Matches executable names; Restart Manager's display names are not used here.
/// The live IsProcessCritical check (see ProcessInspector) backs this list up.
/// </summary>
public static class CriticalProcessGuard
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "csrss", "wininit", "smss", "services", "lsass", "lsaiso", "winlogon",
        "svchost", "system idle process", "secure system", "registry", "memory compression"
    };

    public static bool IsCritical(int pid, string processName)
    {
        if (pid <= 4) return true; // 0 = Idle, 4 = System

        var name = processName ?? string.Empty;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        return Names.Contains(name);
    }
}
