using System.Diagnostics;

namespace ForceDelete.App;

/// <summary>Window showing the persistent deletion history, newest first.</summary>
public sealed class HistoryForm : Form
{
    private readonly DeletionHistory _history;
    private readonly ListView _list = new();

    public HistoryForm(DeletionHistory history)
    {
        _history = history;

        Text = "Deletion History";
        Width = 820;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 320);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("When", 150);
        _list.Columns.Add("Type", 70);
        _list.Columns.Add("Path", 560);

        var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(4) };
        refresh.Click += (_, _) => Reload();

        var openFile = new Button { Text = "Open History File", AutoSize = true, Margin = new Padding(4) };
        openFile.Click += (_, _) => OpenFile();

        var clear = new Button { Text = "Clear History", AutoSize = true, Margin = new Padding(4) };
        clear.Click += (_, _) => ClearHistory();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(6, 4, 6, 4)
        };
        buttons.Controls.AddRange(new Control[] { refresh, openFile, clear });

        Controls.Add(_list);
        Controls.Add(buttons);

        Reload();
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _history.Load().OrderByDescending(x => x.When))
        {
            var when = e.When == DateTime.MinValue ? "" : e.When.ToString("yyyy-MM-dd HH:mm:ss");
            _list.Items.Add(new ListViewItem(new[] { when, e.Type, e.Path }));
        }
        _list.EndUpdate();
        Text = $"Deletion History ({_list.Items.Count})";
    }

    private void OpenFile()
    {
        if (!File.Exists(_history.FilePath))
        {
            MessageBox.Show(this, "No history has been recorded yet.", "Deletion History",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(_history.FilePath) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the file:\n{ex.Message}", "Deletion History",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearHistory()
    {
        if (MessageBox.Show(this,
            "Clear the entire deletion history?\n\nThis only clears the record — it does not restore any files.",
            "Clear History", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _history.Clear();
        Reload();
    }
}
