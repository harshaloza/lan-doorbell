namespace LanDoorbell;

/// <summary>
/// Big "someone is ringing" window that stays on top and flashes until someone answers it.
/// One window per sender; repeated rings update the same window.
/// </summary>
internal sealed class AlertForm : Form
{
    private static readonly Color Calm = Color.FromArgb(21, 36, 63);
    private static readonly Color Flash = Color.FromArgb(20, 125, 180);
    private static readonly TimeSpan FlashFor = TimeSpan.FromSeconds(30);

    private readonly Label titleLabel = new();
    private readonly Label detailLabel = new();
    private readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 500 };
    private TaskCompletionSource<string?>? pendingReply;
    private DateTime flashUntil;
    private int ringCount;
    private bool flashOn;

    /// <summary>Raised when the person answers (sender, reply text).</summary>
    public event Action<string, string>? Answered;

    public string SenderName { get; private set; } = "";

    public AlertForm()
    {
        Text = "Doorbell — " + AppInfo.Name;
        Icon = AppInfo.LoadIcon(SystemInformation.IconSize);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Calm;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 11F);
        Padding = new Padding(LogicalToDeviceUnits(24));

        titleLabel.AutoSize = true;
        titleLabel.Font = new Font("Segoe UI Semibold", 20F);
        titleLabel.MaximumSize = new Size(LogicalToDeviceUnits(560), 0);
        detailLabel.AutoSize = true;
        detailLabel.ForeColor = Color.FromArgb(200, 220, 240);
        detailLabel.Margin = new Padding(3, LogicalToDeviceUnits(6), 3, LogicalToDeviceUnits(18));

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(MakeButton("On my way 👍", () => Answer("On my way 👍"), primary: true));
        buttons.Controls.Add(MakeButton("Busy — 5 minutes", () => Answer("Busy — give me 5 minutes")));
        buttons.Controls.Add(MakeButton("Dismiss", () => Close()));

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(titleLabel);
        layout.Controls.Add(detailLabel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        flashTimer.Tick += (_, _) => FlashTick();
    }

    private Button MakeButton(string text, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(250, 204, 76) : Color.FromArgb(45, 66, 104),
            ForeColor = primary ? Calm : Color.White,
            Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(4), LogicalToDeviceUnits(10), LogicalToDeviceUnits(4)),
            Margin = new Padding(0, 0, LogicalToDeviceUnits(10), 0),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => onClick();
        return b;
    }

    public void AddRing(IncomingRing ring)
    {
        pendingReply?.TrySetResult(null);   // an older ring from the same PC no longer needs an answer
        pendingReply = ring.Reply;
        SenderName = ring.SenderName;
        ringCount++;

        titleLabel.Text = $"🔔 {ring.SenderName} is ringing";
        detailLabel.Text = ringCount == 1
            ? $"at {ring.Time:t}  ·  from {ring.RemoteAddress}"
            : $"{ringCount} rings, the last at {ring.Time:t}  ·  from {ring.RemoteAddress}";

        flashUntil = DateTime.Now + FlashFor;
        flashTimer.Start();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        NativeMethods.FlashUntilFocused(Handle);
    }

    private void Answer(string reply)
    {
        if (pendingReply?.TrySetResult(reply) == true)
            Answered?.Invoke(SenderName, reply);
        Close();
    }

    private void FlashTick()
    {
        if (DateTime.Now > flashUntil)
        {
            flashTimer.Stop();
            BackColor = Calm;
            return;
        }
        flashOn = !flashOn;
        BackColor = flashOn ? Flash : Calm;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        flashTimer.Stop();
        pendingReply?.TrySetResult(null);
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) flashTimer.Dispose();
        base.Dispose(disposing);
    }
}
