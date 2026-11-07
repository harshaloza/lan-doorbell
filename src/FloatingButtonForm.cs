using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace LanDoorbell;

/// <summary>
/// Small round always-on-top bell. Left-click rings everyone; right-drag moves it;
/// right-click (without dragging) opens its menu. It never takes focus from the app you are using,
/// stays out of Alt+Tab, scales with Windows display scaling and remembers its position.
/// </summary>
internal sealed class FloatingButtonForm : Form
{
    private const int LogicalSize = 44;
    private static readonly Color Navy = Color.FromArgb(21, 36, 63);
    private static readonly Color NavyHover = Color.FromArgb(33, 54, 92);
    private static readonly Color NavyPressed = Color.FromArgb(12, 22, 40);
    private static readonly Color BellBlue = Color.FromArgb(69, 185, 229);
    private static readonly Color Gold = Color.FromArgb(250, 204, 76);

    private readonly Func<Task> ringAction;
    private readonly ContextMenuStrip menu;
    private readonly ToolTip toolTip = new() { ShowAlways = true, InitialDelay = 700 };
    private bool hover, leftDown, rightDown, dragging, busy;
    private Point dragStartCursor, dragStartForm;

    /// <summary>Raised after the user finishes dragging the button.</summary>
    public event Action<Point>? PositionCommitted;

    public FloatingButtonForm(Func<Task> ringAction, ContextMenuStrip menu, Point? savedLocation)
    {
        this.ringAction = ringAction;
        this.menu = menu;

        Text = "LAN Doorbell Ring button";
        AccessibleName = "Ring all LAN Doorbell receivers";
        AccessibleRole = AccessibleRole.PushButton;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Navy;
        Cursor = Cursors.Hand;

        ApplyDpiSize(DeviceDpi);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        SetClampedLocation(savedLocation ?? new Point(area.Right - Width - 12, area.Bottom - Height - 8));

        toolTip.SetToolTip(this, "Click: ring all PCs\nRight-drag: move\nRight-click: menu");
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // ---- No focus stealing, no Alt+Tab entry ----
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)NativeMethods.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    // ---- DPI ----
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpiSize(DeviceDpi);   // the monitor it opened on may differ from the primary one
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpiSize(e.DeviceDpiNew);
        SetClampedLocation(Location);
    }

    private void ApplyDpiSize(int dpi)
    {
        int size = (int)Math.Round(LogicalSize * dpi / 96.0);
        Size = new Size(size, size);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, size, size);
        Region?.Dispose();
        Region = new Region(path);
        Invalidate();
    }

    // ---- Position ----
    private void SetClampedLocation(Point proposed)
    {
        var area = Screen.FromRectangle(new Rectangle(proposed, Size)).WorkingArea;
        int x = Math.Max(area.Left, Math.Min(proposed.X, area.Right - Width));
        int y = Math.Max(area.Top, Math.Min(proposed.Y, area.Bottom - Height));
        Location = new Point(x, y);   // never on or under the taskbar
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated) BeginInvoke(() => SetClampedLocation(Location));
    }

    // ---- Mouse ----
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            leftDown = true;
            Invalidate();
        }
        else if (e.Button == MouseButtons.Right)
        {
            rightDown = true;
            dragging = false;
            dragStartCursor = Cursor.Position;
            dragStartForm = Location;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!rightDown) return;
        Point now = Cursor.Position;
        int dx = now.X - dragStartCursor.X, dy = now.Y - dragStartCursor.Y;
        if (!dragging && (Math.Abs(dx) > SystemInformation.DragSize.Width || Math.Abs(dy) > SystemInformation.DragSize.Height))
            dragging = true;
        if (dragging) SetClampedLocation(new Point(dragStartForm.X + dx, dragStartForm.Y + dy));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && leftDown)
        {
            leftDown = false;
            Invalidate();
            if (ClientRectangle.Contains(e.Location)) _ = RingAsync();
        }
        else if (e.Button == MouseButtons.Right && rightDown)
        {
            rightDown = false;
            Capture = false;
            if (dragging)
            {
                dragging = false;
                PositionCommitted?.Invoke(Location);
            }
            else
            {
                // A menu needs a foreground window to close properly when you click elsewhere.
                NativeMethods.SetForegroundWindow(Handle);
                menu.Show(this, e.Location);
            }
        }
    }

    private async Task RingAsync()
    {
        if (busy) return;
        busy = true;
        Invalidate();
        try { await ringAction(); }
        finally
        {
            busy = false;
            Invalidate();
        }
    }

    // ---- Drawing: a vector bell in the logo's colours, sharp at any scale ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Width;

        using (var bg = new SolidBrush(leftDown ? NavyPressed : hover ? NavyHover : Navy))
            g.FillEllipse(bg, 0, 0, s - 1, s - 1);
        using (var rim = new Pen(Color.FromArgb(90, BellBlue), Math.Max(1f, s / 30f)))
            g.DrawEllipse(rim, s * 0.03f, s * 0.03f, s * 0.94f, s * 0.94f);

        if (busy)
        {
            using var dots = new SolidBrush(Gold);
            float r = s * 0.06f;
            for (int i = -1; i <= 1; i++)
                g.FillEllipse(dots, s * 0.5f + i * s * 0.17f - r, s * 0.5f - r, r * 2, r * 2);
            return;
        }

        PointF P(float x, float y) => new(x * s, y * s);
        using (var bell = new GraphicsPath())
        {
            bell.AddArc(0.31f * s, 0.22f * s, 0.38f * s, 0.38f * s, 180, 180);   // dome
            bell.AddLine(P(0.69f, 0.41f), P(0.72f, 0.62f));                       // right side
            bell.AddLine(P(0.72f, 0.62f), P(0.28f, 0.62f));
            bell.AddLine(P(0.28f, 0.62f), P(0.31f, 0.41f));                       // left side
            bell.CloseFigure();
            using var blue = new SolidBrush(BellBlue);
            g.FillPath(blue, bell);
            g.FillEllipse(blue, 0.20f * s, 0.58f * s, 0.60f * s, 0.10f * s);      // flared rim
        }
        using (var gold = new SolidBrush(Gold))
            g.FillEllipse(gold, 0.44f * s, 0.66f * s, 0.12f * s, 0.12f * s);      // clapper
        using (var waves = new Pen(Gold, Math.Max(1.2f, s * 0.045f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(waves, 0.13f * s, 0.20f * s, 0.20f * s, 0.20f * s, 200, 70);
            g.DrawArc(waves, 0.67f * s, 0.20f * s, 0.20f * s, 0.20f * s, 270, 70);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
