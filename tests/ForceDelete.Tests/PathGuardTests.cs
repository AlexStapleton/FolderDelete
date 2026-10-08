using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ForceDelete.Core;

namespace ForceDelete.Tests;

/// <summary>The tier rules, against fixed locations and no filesystem lookups.</summary>
public class PathGuardRuleTests
{
    private static PathGuard Guard() => new(new[]
    {
        new ProtectedLocation(@"C:\Windows", "the Windows folder"),
        new ProtectedLocation(@"C:\Windows\System32", "the Windows system folder"),
        new ProtectedLocation(@"C:\Windows\System32\DriverStore\FileRepository", "the driver store"),
        new ProtectedLocation(@"C:\Users", "the user profiles folder", WarnInside: false),
        new ProtectedLocation(@"C:\Users\alex", "the user profile of alex", WarnInside: false),
    }, canonicalize: p => p);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"c:\WINDOWS\")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\DriverStore")]  // contains the driver store
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\alex")]
    public void IsOrContainsProtectedLocation_IsBlocked(string path)
    {
        Assert.Equal(GuardVerdict.Block, Guard().Check(path).Verdict);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\leftover")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\lgmonitor.inf_amd64_46db")]
    public void InsideProtectedLocation_IsWarned(string path)
    {
        Assert.Equal(GuardVerdict.Warn, Guard().Check(path).Verdict);
    }

    [Theory]
    [InlineData(@"C:\Users\alex\Downloads\junk")]   // profile protects itself, not its contents
    [InlineData(@"C:\Users\alex\.nuget\packages\x")]
    [InlineData(@"C:\WindowsOld")]                  // prefix of a name is not "inside"
    [InlineData(@"C:\Windows.old\stuff")]
    [InlineData(@"D:\Games\thing")]
    public void Unrelated_IsAllowed(string path)
    {
        Assert.Equal(GuardVerdict.Allow, Guard().Check(path).Verdict);
    }

    [Fact]
    public void Warning_NamesTheMostSpecificLocation()
    {
        var result = Guard().Check(@"C:\Windows\System32\DriverStore\FileRepository\pkg");
        Assert.Contains("driver store", result.Reason);
    }

    [Fact]
    public void Block_SaysIsVersusContains()
    {
        Assert.Contains("is the Windows folder", Guard().Check(@"C:\Windows").Reason);
        Assert.Contains("contains", Guard().Check(@"C:\Windows\System32\DriverStore").Reason);
    }
}

/// <summary>Real path resolution: aliases that a text comparison would miss.</summary>
public class PathGuardResolutionTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

    private static PathGuard GuardFor(string protectedDir, bool warnInside = true) =>
        new(new[] { new ProtectedLocation(protectedDir, "the test location", warnInside) });

    [Fact]
    public void ThisMachine_ProtectsTheUsualPlaces()
    {
        var guard = PathGuard.ForThisMachine();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(GuardVerdict.Block, guard.Check(windows).Verdict);
        Assert.Equal(GuardVerdict.Block, guard.Check(Environment.SystemDirectory).Verdict);
        Assert.Equal(GuardVerdict.Block, guard.Check(Path.GetPathRoot(windows)!).Verdict);
        Assert.Equal(GuardVerdict.Block, guard.Check(profile).Verdict);
        Assert.Equal(GuardVerdict.Block, guard.Check(Path.GetDirectoryName(profile)!).Verdict);
        Assert.Equal(GuardVerdict.Block,
            guard.Check(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).Verdict);
        Assert.Equal(GuardVerdict.Warn, guard.Check(Path.Combine(Environment.SystemDirectory,
            @"DriverStore\FileRepository\example.inf_amd64_0000000000000000")).Verdict);
        Assert.Equal(GuardVerdict.Allow, guard.Check(Path.Combine(profile, "Downloads", "x")).Verdict);
        Assert.Equal(GuardVerdict.Allow, guard.Check(Path.GetTempPath()).Verdict);
    }

    [Fact]
    public void ShortName_IsResolved()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var buffer = new StringBuilder(260);
        GetShortPathName(programFiles, buffer, buffer.Capacity);
        var shortName = buffer.ToString();
        if (shortName.Length == 0 || shortName.Equals(programFiles, StringComparison.OrdinalIgnoreCase))
            return; // 8.3 names disabled on this volume: nothing to alias

        Assert.Equal(GuardVerdict.Block, PathGuard.ForThisMachine().Check(shortName).Verdict);
    }

    [Fact]
    public void SubstDrive_IsResolved()
    {
        using var ws = new TestWorkspace();
        var target = ws.CreateDir("protected");
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "ZYXWVUTSRQPONMLKJ".First(c => !used.Contains(c));

        Subst($"{letter}: \"{ws.Root}\"");
        try
        {
            var result = GuardFor(target).Check($@"{letter}:\protected");
            Assert.Equal(GuardVerdict.Block, result.Verdict);
        }
        finally
        {
            Subst($"{letter}: /d");
        }
    }

    [Fact]
    public void JunctionToProtectedLocation_IsNotBlocked()
    {
        // Deleting a junction removes only the link, so it must not be judged by its target.
        using var ws = new TestWorkspace();
        var target = ws.CreateDir("protected");
        ws.CreateJunction("link", target);

        Assert.NotEqual(GuardVerdict.Block, GuardFor(target).Check(ws.Path("link")).Verdict);
    }

    [Fact]
    public void PathThroughJunction_IsResolved()
    {
        // ...but a junction in the MIDDLE of the path leads to the real thing.
        using var ws = new TestWorkspace();
        var real = ws.CreateDir("real");
        var target = ws.CreateDir(@"real\protected");
        ws.CreateJunction("alias", real);

        Assert.Equal(GuardVerdict.Block, GuardFor(target).Check(ws.Path(@"alias\protected")).Verdict);
    }

    [Fact]
    public void MissingPathInsideProtectedLocation_IsStillWarned()
    {
        using var ws = new TestWorkspace();
        var target = ws.CreateDir("protected");

        Assert.Equal(GuardVerdict.Warn, GuardFor(target).Check(ws.Path(@"protected\nope\deeper")).Verdict);
    }

    [Fact]
    public void Engine_RefusesBlockedPath_AndTouchesNothing()
    {
        using var ws = new TestWorkspace();
        var target = ws.CreateDir("protected");
        ws.CreateFile(@"protected\keep.txt");
        var obs = new RecordingObserver();

        var engine = new DeleteEngine(obs, new FakeLockFinder(), new DelegateKiller(_ => true),
            new NoopKillDecision(), new RealOwnership(), guard: GuardFor(target));

        Assert.Equal(ItemStatus.Failed, engine.DeleteItem(ws.Root)); // contains it
        Assert.True(File.Exists(ws.Path(@"protected\keep.txt")));
        Assert.True(obs.LogContains("protected location"));
    }

    private static void Subst(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("subst.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
