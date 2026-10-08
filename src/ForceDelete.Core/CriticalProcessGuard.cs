namespace ForceDelete.Core;

/// <summary>
/// Identifies processes that must never be terminated: killing them blue-screens
/// Windows or is impossible (the System process). Used to refuse the kill offer.
/// </summary>
public static class CriticalProcessGuard
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "csrss", "wininit", "smss", "services", "lsass", "winlogon",
        "system idle process"
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
