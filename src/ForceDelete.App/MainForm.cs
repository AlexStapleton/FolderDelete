using System.ComponentModel;
using ForceDelete.Core;

namespace ForceDelete.App;

public sealed class MainForm : Form, IDeleteObserver, IKillDecision
{
    private readonly ListView _queue = new();
    private readonly TextBox _log = new();
    private readonly Button _addFiles = new();
    private readonly Button _addFolder = new();
    private readonly Button _remove = new();
    private readonly Button _delete = new();
    private readonly Button _historyBtn = new();
    private readonly SplitContainer _split = new();

    private readonly DeletionHistory _history = new();

    // path -> queue row, so status updates find their row.
    private readonly Dictionary<string, ListViewItem> _rows = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "ForceDelete";
        Width = 900;
        Height = 640;
        MinimumSize = new Size(700, 480);

        // AutoSize so the full labels are never clipped to "Add".
        foreach (var b in new[] { _addFiles, _addFolder, _remove, _delete, _historyBtn })
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

        _historyBtn.Text = "History…";
        _historyBtn.Click += (_, _) => { using var f = new HistoryForm(_history); f.ShowDialog(this); };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6, 4, 6, 4) };
        top.Controls.AddRange(new Control[] { _addFiles, _addFolder, _remove, _delete, _historyBtn });

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
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // SplitterDistance must be set after the control has its real (docked) size —
        // setting it during construction (when Height is the default ~150) throws.
        // Clamp to the valid range so it never throws regardless of window size.
        int max = _split.Height - _split.Panel2MinSize;
        _split.SplitterDistance = Math.Clamp(360, _split.Panel1MinSize, Math.Max(_split.Panel1MinSize, max));
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
        var type = Directory.Exists(path) ? "Folder" : "File";
        var row = new ListViewItem(new[] { path, type, ItemStatus.Pending.ToString() });
        _queue.Items.Add(row);
        _rows[path] = row;
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

        SetButtonsEnabled(false);

        Log($"Starting delete of {paths.Count} item(s)…", LogLevel.Info);

        var worker = new BackgroundWorker();
        worker.DoWork += (_, e) =>
        {
            var engine = new DeleteEngine(this, new RestartManagerLockFinder(),
                new ProcessKiller(), this, new OwnershipHelper());
            int done = 0, failed = 0;
            foreach (var p in paths)
            {
                if (engine.DeleteItem(p) == ItemStatus.Done) done++;
                else failed++;
            }
            e.Result = (done, failed);
        };
        worker.RunWorkerCompleted += (_, e) =>
        {
            SetButtonsEnabled(true);

            if (e.Error != null)
            {
                Log($"Run aborted with an unexpected error: {e.Error.Message}", LogLevel.Error);
                MessageBox.Show(this, $"The delete run crashed:\n\n{e.Error.Message}",
                    "ForceDelete", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var (done, failed) = ((int, int))e.Result!;
            Log($"Finished. {done} deleted, {failed} failed.", LogLevel.Info);
            MessageBox.Show(this,
                $"Finished.\n\nDeleted: {done}\nFailed: {failed}\n\n" +
                (failed > 0 ? "See the log at the bottom for why items failed." : "All items deleted."),
                "ForceDelete", MessageBoxButtons.OK,
                failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        };
        worker.RunWorkerAsync();
    }

    /// <summary>
    /// Scrollable confirmation showing every queued path, with an explicit
    /// "Yes, delete these" button — a stock MessageBox can't scroll a long list.
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
        dlg.AcceptButton = yes;
        dlg.CancelButton = cancel;

        return dlg.ShowDialog(this) == DialogResult.Yes;
    }

    private static string DisplayStatus(ItemStatus status) =>
        status == ItemStatus.FixingPermissions ? "Fixing Permissions" : status.ToString();

    private void SetButtonsEnabled(bool enabled)
    {
        _addFiles.Enabled = _addFolder.Enabled = _remove.Enabled = _delete.Enabled = enabled;
    }

    // ---- IDeleteObserver (called from the worker thread) ----

    public void OnStatus(string path, ItemStatus status)
    {
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

    private void Log(string message, LogLevel level)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message, level)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}{Environment.NewLine}");
    }

    // ---- IKillDecision (called from the worker thread; blocks on the UI) ----

    public bool ShouldKill(string path, IReadOnlyList<LockingProcess> processes)
    {
        if (InvokeRequired)
            return (bool)Invoke(() => ShouldKill(path, processes));

        var who = string.Join("\n", processes.Select(p => $"  {p.Name} (PID {p.Pid})"));
        var result = MessageBox.Show(this,
            $"{path}\n\nis locked by:\n{who}\n\nClose these process(es) and delete anyway?",
            "File in use", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }
}
