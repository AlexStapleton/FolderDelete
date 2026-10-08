namespace ForceDelete.Core;

public static class PathUtil
{
    private const int LegacyMaxPath = 260;
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Returns the extended-length form for paths that need it (at/over the legacy
    /// limit), leaving already-prefixed or short paths untouched. The prefix disables
    /// path normalization, so it is applied selectively rather than universally.
    /// </summary>
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path;

        if (path.Length < LegacyMaxPath) return path;

        // UNC: \\server\share\... -> \\?\UNC\server\share\...
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return UncPrefix + path.Substring(2);

        return Prefix + path;
    }
}
