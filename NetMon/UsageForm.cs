using System.Text;

namespace NetMon;

/// <summary>
/// Shows daily and monthly bandwidth usage.  Auto-refreshes every 10 seconds
/// while open.  Supports CSV export and bulk reset.
/// </summary>
public sealed class UsageForm : Form
{
    private readonly UsageStore                   _store;
    private readonly TabControl                   _tabs;
    private readonly System.Windows.Forms.Timer   _refreshTimer;

    public UsageForm(UsageStore store)
    {
        _store = store;

        Text            = "NetMon — Bandwidth Usage";
        Size            = new Size(500, 540);
        MinimumSize     = new Size(380, 300);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        Font            = new Font("Segoe UI", 9f);
        KeyPreview      = true;

        // ─ top caption ─────────────────────────────────────────────────
        var lblCaption = new Label
        {
            Text      = "Usage stored in: " +
                         Path.Combine(Environment.GetFolderPath(
                             Environment.SpecialFolder.ApplicationData), "NetMon", "usage.json"),
            Dock      = DockStyle.Top,
            Height    = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(10, 0, 0, 0),
            Font      = new Font("Segoe UI", 7.5f),
            ForeColor = Color.DimGray,
            BackColor = Color.FromArgb(240, 248, 255)
        };

        // ─ bottom toolbar ──────────────────────────────────────────────
        var bottomBar = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 44,
            BackColor = Color.FromArgb(240, 248, 255)
        };

        var btnExport = new Button
        {
            Text     = "Export CSV…",
            Size     = new Size(100, 28),
            Location = new Point(10, 8),
            FlatStyle = FlatStyle.System
        };
        btnExport.Click += (_, _) => ExportCsv();

        var btnReset = new Button
        {
            Text      = "Reset Data…",
            Size      = new Size(100, 28),
            Location  = new Point(118, 8),
            ForeColor = Color.FromArgb(170, 40, 30),
            FlatStyle = FlatStyle.System
        };
        btnReset.Click += (_, _) => ResetData();

        var btnClose = new Button
        {
            Text         = "Close",
            Size         = new Size(90, 28),
            Anchor       = AnchorStyles.Top | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.System,
            Location     = new Point(bottomBar.Width - 100, 8)
        };
        btnClose.Click += (_, _) => Close();
        CancelButton = btnClose;
        bottomBar.Resize += (_, _) => btnClose.Location = new Point(bottomBar.Width - 100, 8);

        bottomBar.Controls.Add(btnExport);
        bottomBar.Controls.Add(btnReset);
        bottomBar.Controls.Add(btnClose);

        // ─ tabs ────────────────────────────────────────────────────────
        _tabs = new TabControl { Dock = DockStyle.Fill };

        Controls.Add(_tabs);
        Controls.Add(bottomBar);
        Controls.Add(lblCaption);

        RefreshTabs();

        // ─ live refresh (every 10 s while dialog is open) ──────────────
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _refreshTimer.Tick += (_, _) => RefreshTabs();
        _refreshTimer.Start();

        FormClosed += (_, _) => _refreshTimer.Dispose();

        // ─ Esc closes ──────────────────────────────────────────────────
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    // ── tabs ────────────────────────────────────────────────────────────

    private void RefreshTabs()
    {
        int savedIdx = _tabs.SelectedIndex;

        _tabs.SuspendLayout();
        foreach (TabPage page in _tabs.TabPages) page.Dispose();
        _tabs.TabPages.Clear();

        var data = _store.Snapshot();

        _tabs.TabPages.Add(BuildTab("Monthly",
            data.Monthly.OrderByDescending(kv => kv.Key)
                .Select(kv => new[] { kv.Key,
                    MainForm.FormatBytes(kv.Value.BytesReceived),
                    MainForm.FormatBytes(kv.Value.BytesSent),
                    MainForm.FormatBytes(kv.Value.BytesReceived + kv.Value.BytesSent) })));

        _tabs.TabPages.Add(BuildTab("Daily (last 90 days)",
            data.Daily.OrderByDescending(kv => kv.Key).Take(90)
                .Select(kv => new[] { kv.Key,
                    MainForm.FormatBytes(kv.Value.BytesReceived),
                    MainForm.FormatBytes(kv.Value.BytesSent),
                    MainForm.FormatBytes(kv.Value.BytesReceived + kv.Value.BytesSent) })));

        if (savedIdx >= 0 && savedIdx < _tabs.TabPages.Count)
            _tabs.SelectedIndex = savedIdx;
        _tabs.ResumeLayout();
    }

    private static TabPage BuildTab(string title, IEnumerable<string[]> rows)
    {
        var page = new TabPage(title);

        var lv = new ListView
        {
            Dock          = DockStyle.Fill,
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = true,
            Font          = new Font("Consolas", 8.5f),
            BackColor     = Color.FromArgb(248, 252, 255)
        };

        lv.Columns.Add("Period",   120, HorizontalAlignment.Left);
        lv.Columns.Add("Download", 120, HorizontalAlignment.Right);
        lv.Columns.Add("Upload",   120, HorizontalAlignment.Right);
        lv.Columns.Add("Total",    110, HorizontalAlignment.Right);

        int rowIdx = 0;
        foreach (var cols in rows)
        {
            var item = new ListViewItem(cols)
            {
                BackColor = (rowIdx++ % 2 == 0)
                    ? Color.White
                    : Color.FromArgb(235, 244, 252)
            };
            lv.Items.Add(item);
        }

        if (lv.Items.Count == 0)
            lv.Items.Add(new ListViewItem("(no data yet)"));

        page.Controls.Add(lv);
        return page;
    }

    // ── actions ─────────────────────────────────────────────────────────

    private void ExportCsv()
    {
        using var dlg = new SaveFileDialog
        {
            FileName    = $"NetMon_usage_{DateTime.Now:yyyyMMdd}.csv",
            Filter      = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt   = "csv",
            Title        = "Export Usage to CSV"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var data = _store.Snapshot();
            var sb   = new StringBuilder();
            sb.AppendLine("Type,Period,DownloadBytes,UploadBytes,TotalBytes,DownloadHuman,UploadHuman,TotalHuman");

            foreach (var kv in data.Monthly.OrderBy(kv => kv.Key))
                WriteCsvLine(sb, "Monthly", kv.Key, kv.Value);
            foreach (var kv in data.Daily.OrderBy(kv => kv.Key))
                WriteCsvLine(sb, "Daily",   kv.Key, kv.Value);

            File.WriteAllText(dlg.FileName, sb.ToString());
            MessageBox.Show(this, "Export complete.", "NetMon",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export failed:\n\n" + ex.Message, "NetMon",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void WriteCsvLine(StringBuilder sb, string type, string key, DayUsage u)
    {
        long total = u.BytesReceived + u.BytesSent;
        sb.Append(type).Append(',')
          .Append(key).Append(',')
          .Append(u.BytesReceived).Append(',')
          .Append(u.BytesSent).Append(',')
          .Append(total).Append(',')
          .Append(MainForm.FormatBytes(u.BytesReceived)).Append(',')
          .Append(MainForm.FormatBytes(u.BytesSent)).Append(',')
          .Append(MainForm.FormatBytes(total))
          .AppendLine();
    }

    private void ResetData()
    {
        var r = MessageBox.Show(this,
            "This will permanently erase all daily and monthly usage history.\n\n" +
            "Current in-flight traffic will start accumulating from zero.\n\nContinue?",
            "Reset Usage Data",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;

        _store.Clear();
        RefreshTabs();
    }
}
