using ForceDelete.Core;

namespace ForceDelete.Tests;

public class ReparsePointHelperTests
{
    [Fact]
    public void NormalDirectory_IsNotReparsePoint()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("plain");
        Assert.False(ReparsePointHelper.IsReparsePoint(dir));
    }

    [Fact]
    public void NormalFile_IsNotReparsePoint()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("plain.txt");
        Assert.False(ReparsePointHelper.IsReparsePoint(file));
    }

    [Fact]
    public void Junction_IsReparsePoint()
    {
        using var ws = new TestWorkspace();
        var target = ws.CreateDir("target");
        ws.CreateJunction("link", target);
        Assert.True(ReparsePointHelper.IsReparsePoint(ws.Path("link")));
    }
}
