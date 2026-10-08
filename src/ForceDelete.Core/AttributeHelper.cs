namespace ForceDelete.Core;

public static class AttributeHelper
{
    private const FileAttributes Blocking =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    /// <summary>Removes ReadOnly/Hidden/System so a delete can proceed. No-op if none set.</summary>
    public static void ClearBlockingAttributes(string path)
    {
        var attrs = File.GetAttributes(path);
        var cleared = attrs & ~Blocking;
        if (cleared != attrs)
            File.SetAttributes(path, cleared);
    }
}
