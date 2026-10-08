using System.Globalization;
using System.Text;

namespace ForceDelete.App;

public sealed record HistoryEntry(DateTime When, string Type, string Path);

/// <summary>
/// Append-only, file-backed record of items successfully deleted. Persists across
/// runs in history.tsv next to the executable. All writes are best-effort —
/// a history failure must never interfere with the actual delete.
/// The file contains personal paths: keep it out of anything you distribute.
/// </summary>
public sealed class DeletionHistory
{
    public string FilePath { get; }

    public DeletionHistory()
    {
        // Sit next to the executable — predictable and portable. (LocalApplicationData
        // is unreliable when the app runs elevated: it can fail to resolve and leave a
        // literal "%LOCALAPPDATA%" folder under C:\Windows.)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        FilePath = Path.Combine(exeDir, "history.tsv");
    }

    /// <summary>Append one deleted item. Path is written last so a tab inside it is preserved.</summary>
    public void Record(string path, string type)
    {
        var line = $"{DateTime.Now:o}\t{type}\t{path}{Environment.NewLine}";
        try { File.AppendAllText(FilePath, line, Encoding.UTF8); }
        catch { /* history is best-effort; never disrupt deletion */ }
    }

    public IReadOnlyList<HistoryEntry> Load()
    {
        var entries = new List<HistoryEntry>();
        if (!File.Exists(FilePath)) return entries;
        try
        {
            foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var parts = raw.Split('\t', 3); // path may contain tabs; keep it whole
                if (parts.Length < 3) continue;
                var when = DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var w) ? w : DateTime.MinValue;
                entries.Add(new HistoryEntry(when, parts[1], parts[2]));
            }
        }
        catch { /* ignore read/parse errors — return what we have */ }
        return entries;
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* best-effort */ }
    }
}
