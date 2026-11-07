namespace LanDoorbell;

/// <summary>Shows sent and received rings, newest first. Updates live.</summary>
internal sealed class HistoryForm : Form
{
    private readonly ListView list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        Dock = DockStyle.Fill,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    public HistoryForm()
    {
        Text = "Ring history — " + AppInfo.Name;
        Icon = AppInfo.LoadIcon(SystemInformation.IconSize);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(420));
        MinimumSize = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(240));

        list.Columns.Add("Time", LogicalToDeviceUnits(140));
        list.Columns.Add("Direction", LogicalToDeviceUnits(80));
        list.Columns.Add("Computer", LogicalToDeviceUnits(220));
        list.Columns.Add("Result", LogicalToDeviceUnits(260));

        var clear = new Button { Text = "Clear history", AutoSize = true };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete all ring history? This cannot be undone.", AppInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RingHistory.Clear();
        };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(LogicalToDeviceUnits(8)),
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(clear);

        Controls.Add(list);
        Controls.Add(buttons);

        RingHistory.Changed += OnHistoryChanged;
        Reload();
    }

    private void OnHistoryChanged()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(Reload);
    }

    private void Reload()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var e in RingHistory.ReadRecent())
            list.Items.Add(new ListViewItem(new[] { e.Time.ToString("d MMM yyyy  HH:mm"), e.Direction, e.Computer, e.Result }));
        if (list.Items.Count == 0)
            list.Items.Add(new ListViewItem(new[] { "", "", "No rings yet.", "" }) { ForeColor = SystemColors.GrayText });
        list.EndUpdate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) RingHistory.Changed -= OnHistoryChanged;
        base.Dispose(disposing);
    }
}
