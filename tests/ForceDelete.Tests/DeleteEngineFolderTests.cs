using ForceDelete.Core;

namespace ForceDelete.Tests;

/// <summary>Folder removal whose RawDeleteDir is scripted to throw a given exception.</summary>
internal sealed class ThrowingDirEngine : DeleteEngine
{
    private readonly Func<Exception?> _next;
    public int Calls { get; private set; }

    public ThrowingDirEngine(IDeleteObserver observer, Func<Exception?> next)
        : base(observer, new FakeLockFinder(), new DelegateKiller(_ => true),
               new NoopKillDecision(), new RealOwnership())
        => _next = next;

    protected override void RawDeleteDir(string extendedPath)
    {
        Calls++;
        var ex = _next();
        if (ex != null) throw ex;
        Directory.Delete(extendedPath, false);
    }
}

public class DeleteEngineFolderTests
{
    [Fact]
    public void FolderVanishingDuringDelete_IsDone()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("gone");
        var obs = new RecordingObserver();

        var engine = new ThrowingDirEngine(obs, () => new DirectoryNotFoundException());
        Assert.Equal(ItemStatus.Done, engine.DeleteItem(dir));
        Assert.Equal(1, engine.Calls); // no pointless retries
    }

    [Fact]
    public void UnexpectedFolderError_IsReportedWithItsMessage()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("weird");
        var obs = new RecordingObserver();

        var engine = new ThrowingDirEngine(obs, () => new IOException("the disk is haunted"));
        Assert.Equal(ItemStatus.Failed, engine.DeleteItem(dir));
        Assert.True(obs.LogContains("the disk is haunted"));
    }

    [Fact]
    public void DirectoryNotEmpty_IsRetried_ThenSucceeds()
    {
        using var ws = new TestWorkspace();
        var dir = ws.CreateDir("slow");
        var obs = new RecordingObserver();
        int n = 0;

        // ERROR_DIR_NOT_EMPTY (145) twice — Windows' semi-async delete of children.
        var engine = new ThrowingDirEngine(obs, () =>
            ++n <= 2 ? new IOException("The directory is not empty.", unchecked((int)0x80070091)) : null);
        Assert.Equal(ItemStatus.Done, engine.DeleteItem(dir));
        Assert.Equal(3, engine.Calls);
    }
}

public class DeleteEngineCancellationTests
{
    [Fact]
    public void AlreadyCancelled_TouchesNothing()
    {
        using var ws = new TestWorkspace();
        var file = ws.CreateFile("keep.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var engine = new DeleteEngine(new RecordingObserver(), new FakeLockFinder(),
            new DelegateKiller(_ => true), new NoopKillDecision(), new RealOwnership(), cts.Token);

        Assert.Equal(ItemStatus.Cancelled, engine.DeleteItem(file));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void CancelMidFolder_StopsAndLeavesFolder()
    {
        using var ws = new TestWorkspace();
        for (int i = 0; i < 20; i++) ws.CreateFile($@"root\f{i:D2}.txt");
        using var cts = new CancellationTokenSource();
        var obs = new CancellingObserver(cts);

        var engine = new DeleteEngine(obs, new FakeLockFinder(),
            new DelegateKiller(_ => true), new NoopKillDecision(), new RealOwnership(), cts.Token);

        Assert.Equal(ItemStatus.Cancelled, engine.DeleteItem(ws.Path("root")));
        Assert.True(Directory.Exists(ws.Path("root")));
        Assert.True(Directory.GetFiles(ws.Path("root")).Length >= 18);
    }

    /// <summary>Cancels as soon as the first file is reported Done.</summary>
    private sealed class CancellingObserver(CancellationTokenSource cts) : IDeleteObserver
    {
        public void OnStatus(string path, ItemStatus status)
        {
            if (status == ItemStatus.Done) cts.Cancel();
        }
        public void OnLog(string message, LogLevel level) { }
    }
}
