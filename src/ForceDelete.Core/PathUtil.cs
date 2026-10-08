namespace ForceDelete.Core;

public static class PathUtil
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";

    /// <summary>
    /// Returns the \\?\ extended-length form of a fully qualified path. Applied to every
    /// path, not just long ones: the prefix also disables Win32 normalization, which
    /// otherwise strips trailing dots/spaces and maps reserved names ("nul", "con") to
    /// devices — so without it those items are silently skipped instead of deleted.
    /// Relative and device paths are returned unchanged.
    /// </summary>
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(Prefix, StringComparison.Ordinal)) return path;
        if (path.StartsWith(DevicePrefix, StringComparison.Ordinal)) return path;
        if (!Path.IsPathFullyQualified(path)) return path;

        // The prefix passes the path through verbatim, so resolve "." / ".." segments and
        // forward slashes first. (Only then: normalizing would also strip trailing dots.)
        if (HasRelativeSegments(path))
            path = Path.GetFullPath(path);

        // UNC: \\server\share\... -> \\?\UNC\server\share\...
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return UncPrefix + path.Substring(2);

        return Prefix + path;
    }

    /// <summary>Inverse of <see cref="ToExtendedPath"/>, for display and for APIs that reject the prefix.</summary>
    public static string FromExtendedPath(string path)
    {
        if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(UncPrefix.Length);
        if (path.StartsWith(Prefix, StringComparison.Ordinal))
            return path.Substring(Prefix.Length);
        return path;
    }

    private static bool HasRelativeSegments(string path)
    {
        if (path.Contains('/')) return true;
        foreach (var segment in path.Split('\\'))
            if (segment is "." or "..") return true;
        return false;
    }
}
