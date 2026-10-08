namespace ForceDelete.Core;

public static class AttributeHelper
{
    private const FileAttributes Blocking =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    /// <summary>
    /// Removes ReadOnly/Hidden/System so a delete can proceed. Returns false (and does
    /// nothing) if none were set.
    /// </summary>
    public static bool ClearBlockingAttributes(string path)
    {
        var ext = PathUtil.ToExtendedPath(path);
        var attrs = File.GetAttributes(ext);
        var cleared = attrs & ~Blocking;
        if (cleared == attrs) return false;
        File.SetAttributes(ext, cleared);
        return true;
    }
}
