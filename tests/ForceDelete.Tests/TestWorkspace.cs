using System.Diagnostics;

namespace ForceDelete.Tests;

/// <summary>Creates a unique temp directory and best-effort deletes it on Dispose.</summary>
public sealed class TestWorkspace : IDisposable
{
    public string Root { get; }

    public TestWorkspace()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fd_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public string CreateFile(string relative, string content = "x")
    {
        var full = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDir(string relative)
    {
        var full = Path(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a directory junction (does NOT require admin).</summary>
    public void CreateJunction(string linkRelative, string targetFull)
    {
        var link = Path(linkRelative);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{targetFull}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("mklink failed: " + p.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        try { ClearAttributesRecursive(Root); } catch { }
        try { Directory.Delete(Root, recursive: true); return; } catch { }

        // Leftovers with hostile ACLs or odd names: fall back to the engine itself.
        try
        {
            new ForceDelete.Core.DeleteEngine(new SilentObserver(), new FakeLockFinder(),
                new DelegateKiller(_ => false), new NoopKillDecision(),
                new ForceDelete.Core.OwnershipHelper()).DeleteItem(Root);
        }
        catch { }
    }

    private sealed class SilentObserver : ForceDelete.Core.IDeleteObserver
    {
        public void OnStatus(string path, ForceDelete.Core.ItemStatus status) { }
        public void OnLog(string message, ForceDelete.Core.LogLevel level) { }
    }

    private static void ClearAttributesRecursive(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
    }
}
