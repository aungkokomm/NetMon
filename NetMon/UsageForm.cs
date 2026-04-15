namespace NetMon;

/// <summary>
/// Simple dialog that shows daily and monthly bandwidth usage in a ListView.
/// </summary>
public sealed class UsageForm : Form
{
    public UsageForm(UsageStore store)
    {
        Text            = "NetMon – Bandwidth Usage";
        Size            = new Size(420, 480);
        MinimumSize     = new Size(360, 300);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        Font            = new Font("Segoe UI", 9f);

        // ─ toolbar ───────────────────────────────────────────────────────
        var toolbar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 36,
            BackColor = Color.FromArgb(240, 248, 255)
        };

        var lblCaption = new Label
        {
            Text      = "Usage stored in: " +
                         Path.Combine(Environment.GetFolderPath(
                             Environment.SpecialFolder.ApplicationData), "NetMon", "usage.json"),
            AutoSize  = false,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(8, 0, 0, 0),
            Font      = new Font("Segoe UI", 7.5f),
            ForeColor = Color.DimGray
        };
        toolbar.Controls.Add(lblCaption);
        Controls.Add(toolbar);

        // ─ tab control ───────────────────────────────────────────────────
        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);

        var data = store.Snapshot();

        tabs.TabPages.Add(BuildTab("Monthly",
            data.Monthly
                .OrderByDescending(kv => kv.Key)
                .Select(kv => new[] {
                    kv.Key,
                    MainForm.FormatBytes(kv.Value.BytesReceived),
                    MainForm.FormatBytes(kv.Value.BytesSent),
                    MainForm.FormatBytes(kv.Value.BytesReceived + kv.Value.BytesSent)
                })));

        tabs.TabPages.Add(BuildTab("Daily (last 90 days)",
            data.Daily
                .OrderByDescending(kv => kv.Key)
                .Take(90)
                .Select(kv => new[] {
                    kv.Key,
                    MainForm.FormatBytes(kv.Value.BytesReceived),
                    MainForm.FormatBytes(kv.Value.BytesSent),
                    MainForm.FormatBytes(kv.Value.BytesReceived + kv.Value.BytesSent)
                })));
    }

    private static TabPage BuildTab(string title, IEnumerable<string[]> rows)
    {
        var page = new TabPage(title);

        var lv = new ListView
        {
            Dock        = DockStyle.Fill,
            View        = View.Details,
            FullRowSelect = true,
            GridLines   = true,
            Font        = new Font("Consolas", 8.5f),
            BackColor   = Color.FromArgb(248, 252, 255)
        };

        lv.Columns.Add("Period",     110, HorizontalAlignment.Left);
        lv.Columns.Add("Download",   100, HorizontalAlignment.Right);
        lv.Columns.Add("Upload",     100, HorizontalAlignment.Right);
        lv.Columns.Add("Total",       90, HorizontalAlignment.Right);

        // Alternating row colours
        int rowIndex = 0;
        foreach (var cols in rows)
        {
            var item = new ListViewItem(cols)
            {
                BackColor = (rowIndex++ % 2 == 0)
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
}
