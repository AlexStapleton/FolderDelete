using ForceDelete.Core;

namespace ForceDelete.Tests;

public class PathUtilTests
{
    [Fact]
    public void ShortLocalPath_GetsPrefix()
    {
        // Always prefixed: it is what keeps "name." / "name " / "nul" from being mangled.
        Assert.Equal(@"\\?\C:\temp\file.txt", PathUtil.ToExtendedPath(@"C:\temp\file.txt"));
    }

    [Fact]
    public void TrailingDot_IsPreserved()
    {
        Assert.Equal(@"\\?\C:\temp\bad.", PathUtil.ToExtendedPath(@"C:\temp\bad."));
    }

    [Fact]
    public void AlreadyPrefixed_IsUnchanged()
    {
        Assert.Equal(@"\\?\C:\temp\file.txt", PathUtil.ToExtendedPath(@"\\?\C:\temp\file.txt"));
    }

    [Fact]
    public void RelativePath_IsUnchanged()
    {
        Assert.Equal(@"temp\file.txt", PathUtil.ToExtendedPath(@"temp\file.txt"));
    }

    [Fact]
    public void DevicePath_IsUnchanged()
    {
        Assert.Equal(@"\\.\PhysicalDrive0", PathUtil.ToExtendedPath(@"\\.\PhysicalDrive0"));
    }

    [Fact]
    public void PathWithDotSegments_IsNormalizedBeforePrefixing()
    {
        Assert.Equal(@"\\?\C:\b\c.txt", PathUtil.ToExtendedPath(@"C:\a\..\b\c.txt"));
    }

    [Fact]
    public void LongLocalPath_GetsPrefix()
    {
        var longPath = @"C:\" + new string('a', 300);
        Assert.Equal(@"\\?\" + longPath, PathUtil.ToExtendedPath(longPath));
    }

    [Fact]
    public void UncPath_GetsUncPrefix()
    {
        var unc = @"\\server\share\" + new string('b', 300);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('b', 300), PathUtil.ToExtendedPath(unc));
    }

    [Theory]
    [InlineData(@"C:\temp\bad.")]
    [InlineData(@"\\server\share\x")]
    public void FromExtendedPath_RoundTrips(string path)
    {
        Assert.Equal(path, PathUtil.FromExtendedPath(PathUtil.ToExtendedPath(path)));
    }
}
