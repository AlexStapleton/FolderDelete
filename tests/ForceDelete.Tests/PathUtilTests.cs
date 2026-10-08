using ForceDelete.Core;

namespace ForceDelete.Tests;

public class PathUtilTests
{
    [Fact]
    public void ShortLocalPath_IsUnchanged()
    {
        Assert.Equal(@"C:\temp\file.txt", PathUtil.ToExtendedPath(@"C:\temp\file.txt"));
    }

    [Fact]
    public void AlreadyPrefixed_IsUnchanged()
    {
        Assert.Equal(@"\\?\C:\temp\file.txt", PathUtil.ToExtendedPath(@"\\?\C:\temp\file.txt"));
    }

    [Fact]
    public void LongLocalPath_GetsPrefix()
    {
        var longPath = @"C:\" + new string('a', 300);
        Assert.Equal(@"\\?\" + longPath, PathUtil.ToExtendedPath(longPath));
    }

    [Fact]
    public void LongUncPath_GetsUncPrefix()
    {
        var longUnc = @"\\server\share\" + new string('b', 300);
        var result = PathUtil.ToExtendedPath(longUnc);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('b', 300), result);
    }
}
