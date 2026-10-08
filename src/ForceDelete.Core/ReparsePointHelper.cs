namespace ForceDelete.Core;

public static class ReparsePointHelper
{
    /// <summary>
    /// True if the path is a junction, symlink, mount point, or other reparse point.
    /// Callers must delete the link itself and never recurse through it.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(PathUtil.ToExtendedPath(path));
            return attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
