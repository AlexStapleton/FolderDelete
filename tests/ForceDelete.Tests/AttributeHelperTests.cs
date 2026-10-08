using ForceDelete.Core;

namespace ForceDelete.Tests;

public class AttributeHelperTests
{
    [Fact]
    public void ClearsReadOnlyHiddenSystem_AllowingDelete()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("locked.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

        AttributeHelper.ClearBlockingAttributes(file);

        var attrs = File.GetAttributes(file);
        Assert.False(attrs.HasFlag(FileAttributes.ReadOnly));
        Assert.False(attrs.HasFlag(FileAttributes.Hidden));
        Assert.False(attrs.HasFlag(FileAttributes.System));

        File.Delete(file); // must not throw now
        Assert.False(File.Exists(file));
    }
}
