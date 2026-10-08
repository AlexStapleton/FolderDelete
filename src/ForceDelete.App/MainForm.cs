using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using ForceDelete.Core;

namespace ForceDelete.App;

public sealed class MainForm : Form, IDeleteObserver, IKillDecision
{
    // Above this the log keeps only its newest half; History is the permanent record.
    private const int MaxLogChars = 1_000_000;

    private readonly ListView _queue = new();
    private readonly TextBox _log = new();
    private readonly Button _addFiles = new();
    private readonly Button _addFolder = new();
    private readonly Button _remove = new();
    private readonly Button _delete = new();
    private readonly Button _stop = new();
    private readonly Button _historyBtn = new();
    private readonly SplitContainer _split = new();

    private readonly DeletionHistory _history = new();
    private readonly IReadOnlyList<string> _missingPrivileges;

    // path -> queue row, so status updates find their row. UI thread only.
    private readonly Dictionary<string, ListViewItem> _rows = new(StringComparer.OrdinalIgnoreCase);

    // Log lines from any thread, appended to the TextBox in batches by _logTimer:
    // one UI message per tick instead of one per file.
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 150 };

    // Run state (UI thread). _runPaths is an immutable snapshot the worker reads to skip
    // marshalling status updates for files inside a tree — only queue rows are shown.
    private BackgroundWorker? _worker;
    private CancellationTokenSource? _cts;
    private volatile HashSet<string>? _runPaths;
    private bool _closeWhenDone;

    public MainForm(IReadOnlyList<string> missingPrivileges)
    {
        _missingPrivileges = missingPrivileges;

        Text = "ForceDelete";
        Width = 900;
        Height = 640;
        MinimumSize = new Size(700, 480);

        // AutoSize so the full labels are never clipped to "Add".
        foreach (var b in new[] { _addFiles, _addFolder, _remove, _delete, _stop, _historyBtn })
        {
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Margin = new Padding(4, 4, 4, 4);
            b.Padding = new Padding(8, 2, 8, 2);
        }

        _addFiles.Text = "Add Files…";
        _addFiles.Click += (_, _) => AddFiles();

        _addFolder.Text = "Add Folder…";
        _addFolder.Click += (_, _) => AddFolder();

        _remove.Text = "Remove Selected";
        _remove.Click += (_, _) => RemoveSelected();

        _delete.Text = "Delete All";
        _delete.Click += (_, _) => DeleteAll();

        _stop.Text = "Stop";
        _stop.Enabled = false;
        _stop.Click += (_, _) => RequestStop();

        _historyBtn.Text = "History…";
        _historyBtn.Click += (_, _) => { using var f = new HistoryForm(_history); f.ShowDialog(this); };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 4, 6, 4) };
        top.Controls.AddRange(new Control[] { _addFiles, _addFolder, _remove, _delete, _stop, _historyBtn });

        _queue.View = View.Details;
        _queue.FullRowSelect = true;
        _queue.Dock = DockStyle.Fill;
        _queue.Columns.Add("Path", 560);
        _queue.Columns.Add("Type", 80);
        _queue.Columns.Add("Status", 160);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.Font = new Font(FontFamily.GenericMonospace, 8.5f);

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.Panel1.Controls.Add(_queue);
        _split.Panel2.Controls.Add(_log);

        Controls.Add(_split);
        Controls.Add(top);

        _logTimer.Tick += (_, _) => FlushLog();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // SplitterDistance must be set after the control has its real (docked) size —
        // setting it during construction (when Height is the default ~150) throws.
        // Clamp to the valid range so it never throws regardless of window size.
        int max = _split.Height - _split.Panel2MinSize;
        _split.SplitterDistance = Math.Clamp(360, _split.Panel1MinSize, Math.Max(_split.Panel1MinSize, max));

        _logTimer.Start();

        if (_missingPrivileges.Count > 0)
            Log($"Could not enable {string.Join(", ", _missingPrivileges)}. Taking ownership of " +
                "files owned by other accounts may fail — make sure the app is running as administrator.",
                LogLevel.Warning);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_worker != null && !_closeWhenDone)
        {
            // Never let closing the window kill the process mid-delete (e.g. between
            // taking ownership and granting access). Stop cleanly, then close.
            e.Cancel = true;
            var answer = MessageBox.Show(this,
                "A delete is still running.\n\nStop it and close once the current item finishes?",
                "ForceDelete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes)
            {
                _closeWhenDone = true;
                RequestStop();
            }
            return;
        }
        if (_worker != null) e.Cancel = true; // already stopping; RunWorkerCompleted will close

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _logTimer.Stop();
        _logTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Select file(s) to delete" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            foreach (var f in dlg.FileNames) AddToQueue(f);
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a folder to delete" };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
            AddToQueue(dlg.SelectedPath);
    }

    private void AddToQueue(string path)
    {
        if (_rows.ContainsKey(path)) return;
        var row = new ListViewItem(new[] { path, DescribeType(path), ItemStatus.Pending.ToString() });
        _queue.Items.Add(row);
        _rows[path] = row;
    }

    /// <summary>Directory.Exists would call an unreadable folder a "File"; ask the filesystem directly.</summary>
    private static string DescribeType(string path)
    {
        try
        {
            return File.GetAttributes(PathUtil.ToExtendedPath(path)).HasFlag(FileAttributes.Directory)
                ? "Folder" : "File";
        }
        catch
        {
            return "Unknown";
        }
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem row in _queue.SelectedItems)
        {
            _rows.Remove(row.Text);
            row.Remove();
        }
    }

    private void DeleteAll()
    {
        var paths = _rows.Keys.ToList();
        if (paths.Count == 0) return;

        if (!ConfirmDelete(paths)) return;

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _cts = cts;
        _runPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        Log($"Starting delete of {paths.Count} item(s)…", LogLevel.Info);

        var worker = new BackgroundWorker();
        worker.DoWork += (_, e) =>
        {
            var engine = new DeleteEngine(this, new RestartManagerLockFinder(),
                new ProcessKiller(), this, new OwnershipHelper(), token);
            int done = 0, failed = 0, cancelled = 0;
            foreach (var p in paths)
            {
                if (token.IsCancellationRequested) break; // the rest stay Pending
                switch (engine.DeleteItem(p))
                {
                    case ItemStatus.Done: done++; break;
                    case ItemStatus.Cancelled: cancelled++; break;
                    default: failed++; break;
                }
            }
            e.Result = new RunSummary(done, failed, cancelled, paths.Count - done - failed - cancelled);
        };
        worker.RunWorkerCompleted += (_, e) =>
        {
            _worker = null;
            _runPaths = null;
            _cts = null;
            cts.Dispose();
            worker.Dispose();
            SetRunning(false);

            if (e.Error != null)
            {
                Log($"Run aborted with an unexpected error: {e.Error.Message}", LogLevel.Error);
                FlushLog();
                if (_closeWhenDone) { Close(); return; }
                MessageBox.Show(this, $"The delete run crashed:\n\n{e.Error.Message}",
                    "ForceDelete", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var s = (RunSummary)e.Result!;
            Log($"Finished. {s.Done} deleted, {s.Failed} failed" +
                (s.Cancelled + s.NotStarted > 0 ? $", {s.Cancelled} stopped part-way, {s.NotStarted} not started." : "."),
                LogLevel.Info);
            FlushLog();

            if (_closeWhenDone) { Close(); return; }

            bool problems = s.Failed + s.Cancelled + s.NotStarted > 0;
            var text = $"Finished.\n\nDeleted: {s.Done}\nFailed: {s.Failed}";
            if (s.Cancelled + s.NotStarted > 0)
                text += $"\nStopped part-way: {s.Cancelled}\nNot started: {s.NotStarted}";
            text += "\n\n" + (s.Failed > 0 ? "See the log at the bottom for why items failed."
                : problems ? "The run was stopped before finishing." : "All items deleted.");
            MessageBox.Show(this, text, "ForceDelete", MessageBoxButtons.OK,
                problems ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        };

        _worker = worker;
        SetRunning(true);
        worker.RunWorkerAsync();
    }

    private sealed record RunSummary(int Done, int Failed, int Cancelled, int NotStarted);

    private void RequestStop()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        _stop.Enabled = false;
        Log("Stopping after the current item…", LogLevel.Warning);
    }

    /// <summary>
    /// Scrollable confirmation showing every queued path, with an explicit
    /// "Yes, delete these" button — a stock MessageBox can't scroll a long list.
    /// Cancel has focus and Enter does not confirm: a reflexive keypress must not
    /// start a permanent delete.
    /// </summary>
    private bool ConfirmDelete(IReadOnlyList<string> paths)
    {
        using var dlg = new Form
        {
            Text = "Confirm delete",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = false,
            Width = 640,
            Height = 460,
            MinimumSize = new Size(420, 300)
        };

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10, 8, 10, 0),
            Text = $"This will PERMANENTLY delete the following {paths.Count} item(s). There is no undo."
        };

        var list = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = string.Join(Environment.NewLine, paths),
            Font = new Font(FontFamily.GenericMonospace, 8.5f)
        };

        var yes = new Button { Text = "Yes, delete these", DialogResult = DialogResult.Yes, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        buttons.Controls.AddRange(new Control[] { cancel, yes });

        dlg.Controls.Add(list);
        dlg.Controls.Add(header);
        dlg.Controls.Add(buttons);
        dlg.CancelButton = cancel;
        dlg.Load += (_, _) => dlg.ActiveControl = cancel;

        return dlg.ShowDialog(this) == DialogResult.Yes;
    }

    private static string DisplayStatus(ItemStatus status) => status switch
    {
        ItemStatus.FixingPermissions => "Fixing Permissions",
        ItemStatus.Cancelled => "Stopped",
        _ => status.ToString()
    };

    private void SetRunning(bool running)
    {
        _addFiles.Enabled = _addFolder.Enabled = _remove.Enabled = _delete.Enabled = !running;
        _stop.Enabled = running;
    }

    // ---- IDeleteObserver (called from the worker thread) ----

    public void OnStatus(string path, ItemStatus status)
    {
        // Called for every file inside a tree; only queue rows are displayed, so filter
        // here rather than marshalling thousands of no-op updates to the UI thread.
        var run = _runPaths;
        if (run is null || !run.Contains(path)) return;

        if (InvokeRequired) { BeginInvoke(() => OnStatus(path, status)); return; }
        if (!_rows.TryGetValue(path, out var row)) return;

        if (status == ItemStatus.Done)
        {
            // Success: record to the persistent activity history, then drop it from the queue.
            _history.Record(path, row.SubItems[1].Text);
            _rows.Remove(path);
            row.Remove();
        }
        else
        {
            row.SubItems[2].Text = DisplayStatus(status);
        }
    }

    public void OnLog(string message, LogLevel level) => Log(message, level);

    private void Log(string message, LogLevel level) =>
        _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}{Environment.NewLine}");

    /// <summary>Moves queued log lines into the TextBox. UI thread only.</summary>
    private void FlushLog()
    {
        if (_pendingLog.IsEmpty) return;

        var batch = new StringBuilder();
        while (_pendingLog.TryDequeue(out var line)) batch.Append(line);

        if (_log.TextLength + batch.Length <= MaxLogChars)
        {
            _log.AppendText(batch.ToString());
            return;
        }

        // Keep the newest half, cut at a line boundary.
        var all = _log.Text + batch;
        var kept = all.Substring(all.Length - MaxLogChars / 2);
        int firstLine = kept.IndexOf('\n');
        if (firstLine >= 0) kept = kept.Substring(firstLine + 1);
        _log.Text = "[… earlier log lines trimmed …]" + Environment.NewLine + kept;
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    // ---- IKillDecision (called from the worker thread; blocks on the UI) ----

    public bool ShouldKill(string path, IReadOnlyList<LockingProcess> processes)
    {
        if (InvokeRequired)
            return (bool)Invoke(() => ShouldKill(path, processes));

        // A stop was requested: don't hold the run open on a question.
        if (_cts is null || _cts.IsCancellationRequested) return false;

        FlushLog(); // the "held by …" lines should be visible behind the prompt
        var who = string.Join("\n", processes.Select(p => $"  {p.Name} (PID {p.Pid})"));
        var result = MessageBox.Show(this,
            $"{path}\n\nis locked by:\n{who}\n\nClose these process(es) and delete anyway?\n\n" +
            "Unsaved work in them will be lost. If you answer No, you won't be asked again " +
            "about these processes during this run.",
            "File in use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }
}
