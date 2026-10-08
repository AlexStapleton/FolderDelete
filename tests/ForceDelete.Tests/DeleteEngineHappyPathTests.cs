using ForceDelete.Core;

namespace ForceDelete.Tests;

public class DeleteEngineHappyPathTests
{
    [Fact]
    public void DeletesPlainFile()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("a.txt");
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(file);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeletesReadOnlyFile()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("ro.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(file);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeletesNestedFolderTree()
    {
        using var ws = new TestWorkspace();
        ws.CreateFile(@"root\a.txt");
        ws.CreateFile(@"root\sub\b.txt");
        ws.CreateFile(@"root\sub\deep\c.txt");
        var root = ws.Path("root");
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(root);

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void MissingItem_ReportsDone()
    {
        using var ws = new TestWorkspace();
        var obs = new RecordingObserver();

        var status = EngineFactory.Simple(obs).DeleteItem(ws.Path("nope.txt"));

        Assert.Equal(ItemStatus.Done, status);
    }

    [Fact]
    public void JunctionInsideTree_IsDeletedButTargetSurvives()
    {
        using var ws = new TestWorkspace();
        // Target lives OUTSIDE the tree we delete.
        var target = ws.CreateDir("outside_target");
        var sentinel = ws.CreateFile(@"outside_target\keep.txt");

        ws.CreateDir("root");
        ws.CreateFile(@"root\a.txt");
        ws.CreateJunction(@"root\link", target);

        var obs = new RecordingObserver();
        var status = EngineFactory.Simple(obs).DeleteItem(ws.Path("root"));

        Assert.Equal(ItemStatus.Done, status);
        Assert.False(Directory.Exists(ws.Path("root")));
        Assert.True(File.Exists(sentinel)); // did NOT recurse through the junction
    }
}
