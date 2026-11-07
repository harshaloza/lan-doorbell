using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace LanDoorbell;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new DoorbellContext());
    }
}

internal sealed class AppSettings
{
    public List<string> Receivers { get; set; } = new() { "RECEIVER-PC" };
    public int Port { get; set; } = 45872;
    public string SharedSecret { get; set; } = "CHANGE-THIS-TO-A-LONG-SECRET";
    public bool ReceiverEnabled { get; set; }
    public bool StartWithWindows { get; set; }
    public bool TemporarilyUnmuteForDoorbell { get; set; }
}

internal sealed class DoorbellContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem receiverToggle;
    private readonly ToolStripMenuItem floatingToggle;
    private FloatingButtonForm? floatingForm;
    private AppSettings settings;
    private readonly string settingsPath;
    private TcpListener? listener;
    private CancellationTokenSource? listenCts;
    private static readonly object soundLock = new();
    private static readonly byte[] soundBytes = LoadEmbeddedSound();
    private bool exiting;

    private static byte[] LoadEmbeddedSound()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LanDoorbell.ding-dong.wav")
            ?? throw new InvalidOperationException("Embedded ding-dong.wav is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public DoorbellContext()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanDoorbell");
        Directory.CreateDirectory(folder);
        settingsPath = Path.Combine(folder, "settings.json");
        settings = LoadSettings();

        receiverToggle = new ToolStripMenuItem("Enable receiver on this PC", null, (_, _) => ToggleReceiver());
        floatingToggle = new ToolStripMenuItem("Show floating Ring button", null, (_, _) => ToggleFloatingButton());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Ring all selected PCs", null, async (_, _) => await RingAsync()));
        menu.Items.Add(new ToolStripMenuItem("Test ding-dong sound", null, (_, _) => PlayDoorbell()));
        menu.Items.Add(new ToolStripMenuItem("Settings and receivers…", null, (_, _) => ShowSettings()));
        menu.Items.Add(floatingToggle);
        menu.Items.Add(receiverToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        Icon appIcon;
        try { appIcon = new Icon(Path.Combine(AppContext.BaseDirectory, "LanDoorbell.ico")); }
        catch { appIcon = SystemIcons.Information; }

        tray = new NotifyIcon
        {
            Icon = appIcon,
            Text = "LAN Doorbell Pro",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowSettings();

        ApplyStartupSetting();
        if (settings.ReceiverEnabled) StartReceiver();
        ShowFloatingButton();
        UpdateTray();
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                string jsonText = File.ReadAllText(settingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(jsonText);
                if (loaded != null)
                {
                    // Backward compatibility with earlier single-receiver settings.
                    using var doc = JsonDocument.Parse(jsonText);
                    if (doc.RootElement.TryGetProperty("ReceiverHost", out var host) && loaded.Receivers.Count == 0)
                        loaded.Receivers.Add(host.GetString() ?? "");
                    if (doc.RootElement.TryGetProperty("ReceiverIp", out var ip) && loaded.Receivers.Count == 0)
                        loaded.Receivers.Add(ip.GetString() ?? "");
                    loaded.Receivers = loaded.Receivers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (loaded.Receivers.Count == 0) loaded.Receivers.Add("RECEIVER-PC");
                    return loaded;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    private void SaveSettings()
    {
        settings.Receivers = settings.Receivers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        ApplyStartupSetting();
        UpdateTray();
    }

    private void ApplyStartupSetting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (settings.StartWithWindows) key.SetValue("LanDoorbell", $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue("LanDoorbell", false);
        }
        catch { }
    }

    private void UpdateTray()
    {
        receiverToggle.Checked = settings.ReceiverEnabled;
        floatingToggle.Checked = floatingForm != null && !floatingForm.IsDisposed && floatingForm.Visible;
        tray.Text = settings.ReceiverEnabled ? "LAN Doorbell Pro (receiver enabled)" : "LAN Doorbell Pro";
    }

    private void ShowFloatingButton()
    {
        if (floatingForm == null || floatingForm.IsDisposed)
            floatingForm = new FloatingButtonForm(async () => await RingAsync());
        floatingForm.Show();
        floatingForm.BringToFront();
        UpdateTray();
    }

    private void ToggleFloatingButton()
    {
        if (floatingForm != null && !floatingForm.IsDisposed && floatingForm.Visible)
        {
            floatingForm.Hide();
            UpdateTray();
        }
        else
        {
            ShowFloatingButton();
        }
    }

    private void ToggleReceiver()
    {
        settings.ReceiverEnabled = !settings.ReceiverEnabled;
        if (settings.ReceiverEnabled) StartReceiver(); else StopReceiver();
        SaveSettings();
        tray.ShowBalloonTip(1800, "LAN Doorbell Pro", settings.ReceiverEnabled ? "Receiver enabled." : "Receiver disabled.", ToolTipIcon.Info);
    }

    private void StartReceiver()
    {
        StopReceiver();
        listenCts = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, settings.Port);
        try
        {
            listener.Start();
            _ = ListenLoopAsync(listener, listenCts.Token);
        }
        catch (Exception ex)
        {
            settings.ReceiverEnabled = false;
            tray.ShowBalloonTip(3500, "LAN Doorbell Pro", "Could not listen on port " + settings.Port + ": " + ex.Message, ToolTipIcon.Error);
            try { listener.Stop(); } catch { }
            listener = null;
        }
        UpdateTray();
    }

    private void StopReceiver()
    {
        try { listenCts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        listenCts?.Dispose();
        listenCts = null;
        listener = null;
        UpdateTray();
    }

    private async Task ListenLoopAsync(TcpListener tcp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await tcp.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!token.IsCancellationRequested) await Task.Delay(250); }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                string? received = await reader.ReadLineAsync(token);
                string expected = "RING:" + settings.SharedSecret;
                bool allowed = received != null && Encoding.UTF8.GetByteCount(received) <= 512 &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(allowed ? "OK\n" : "DENIED\n"), token);
                if (allowed)
                {
                    PlayDoorbell();
                    tray.ShowBalloonTip(4500, "🔔 Doorbell", "Someone is ringing this PC.", ToolTipIcon.Info);
                }
            }
            catch { }
        }
    }

    private void PlayDoorbell()
    {
        bool unmute = settings.TemporarilyUnmuteForDoorbell;
        _ = Task.Run(() =>
        {
            lock (soundLock)
            {
                bool wasMuted = false;
                MMDevice? device = null;
                try
                {
                    if (unmute)
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        wasMuted = device.AudioEndpointVolume.Mute;
                        if (wasMuted) device.AudioEndpointVolume.Mute = false;
                    }
                    using var ms = new MemoryStream(soundBytes, writable: false);
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                }
                catch
                {
                    try { System.Media.SystemSounds.Beep.Play(); } catch { }
                }
                finally
                {
                    if (unmute && wasMuted && device != null)
                    {
                        try { device.AudioEndpointVolume.Mute = true; } catch { }
                    }
                    device?.Dispose();
                }
            }
        });
    }

    private async Task RingAsync()
    {
        var targets = settings.Receivers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("Add at least one receiver in Settings.", "LAN Doorbell Pro");
            ShowSettings();
            return;
        }

        var results = await Task.WhenAll(targets.Select(RingOneAsync));
        string summary = string.Join(Environment.NewLine, results.Select(r => $"{r.Host}: {(r.Success ? "RING SENT" : "FAILED — " + r.Error)}"));
        tray.ShowBalloonTip(2500, "LAN Doorbell Pro", $"{results.Count(r => r.Success)} of {results.Length} receiver(s) reached.", ToolTipIcon.Info);
        if (results.Any(r => !r.Success))
            MessageBox.Show(summary, "LAN Doorbell — Send results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task<(string Host, bool Success, string Error)> RingOneAsync(string host)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(host, settings.Port, timeout.Token);
            using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("RING:" + settings.SharedSecret + "\n"), timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            string? response = await reader.ReadLineAsync(timeout.Token);
            return response == "OK" ? (host, true, "") : (host, false, "receiver rejected secret");
        }
        catch (Exception ex) { return (host, false, ex.Message); }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(settings);
        if (form.ShowDialog() == DialogResult.OK)
        {
            settings = form.Result;
            SaveSettings();
            if (settings.ReceiverEnabled) StartReceiver(); else StopReceiver();
            SaveSettings();
        }
    }

    private void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        StopReceiver();
        try { floatingForm?.Close(); floatingForm?.Dispose(); } catch { }
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        StopReceiver();
        base.ExitThreadCore();
    }
}

internal sealed class FloatingButtonForm : Form
{
    private readonly Func<Task> ringAction;
    private readonly Button ringButton = new();
    private bool dragging;
    private Point dragStart;
    private Point formStart;

    public FloatingButtonForm(Func<Task> onRing)
    {
        ringAction = onRing;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(44, 44);
        MinimumSize = MaximumSize = Size;
        BackColor = Color.FromArgb(20, 36, 63);
        Padding = Padding.Empty;

        ringButton.Text = "🔔";
        ringButton.Font = new Font("Segoe UI Emoji", 18F, FontStyle.Regular);
        ringButton.ForeColor = Color.White;
        ringButton.BackColor = Color.FromArgb(20, 125, 180);
        ringButton.FlatStyle = FlatStyle.Flat;
        ringButton.FlatAppearance.BorderSize = 0;
        ringButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 145, 205);
        ringButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(15, 100, 150);
        ringButton.Cursor = Cursors.Hand;
        ringButton.SetBounds(2, 2, 40, 40);
        ringButton.AccessibleName = "Ring all configured LAN Doorbell receivers";
        ringButton.TabStop = false;
        ringButton.Click += async (_, _) =>
        {
            ringButton.Enabled = false;
            string oldText = ringButton.Text;
            ringButton.Text = "…";
            try { await ringAction(); }
            finally { ringButton.Text = oldText; ringButton.Enabled = true; }
        };

        // Right-click and drag to move; left-click always rings.
        ringButton.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            dragging = true;
            dragStart = Cursor.Position;
            formStart = Location;
        };
        ringButton.MouseMove += (_, _) =>
        {
            if (!dragging) return;
            Point current = Cursor.Position;
            SetClampedLocation(new Point(formStart.X + current.X - dragStart.X,
                                         formStart.Y + current.Y - dragStart.Y));
        };
        ringButton.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) dragging = false; };
        Controls.Add(ringButton);

        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        SetClampedLocation(new Point(area.Right - Width - 12, area.Bottom - Height - 8));
        Shown += (_, _) => SetClampedLocation(Location);
        LocationChanged += (_, _) => KeepInsideWorkingArea();
    }

    private void SetClampedLocation(Point proposed)
    {
        var area = Screen.FromPoint(proposed).WorkingArea;
        int x = Math.Max(area.Left, Math.Min(proposed.X, area.Right - Width));
        int y = Math.Max(area.Top, Math.Min(proposed.Y, area.Bottom - Height));
        // The button is constrained to the usable desktop area, never onto/under the taskbar.
        Location = new Point(x, y);
    }

    private void KeepInsideWorkingArea()
    {
        if (!IsHandleCreated || dragging) return;
        var area = Screen.FromPoint(Location).WorkingArea;
        int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        if (x != Left || y != Top) BeginInvoke(new Action(() => Location = new Point(x, y)));
    }
}

internal sealed class SettingsForm : Form
{
    private readonly TextBox receiversBox = new();
    private readonly NumericUpDown portBox = new();
    private readonly TextBox secretBox = new();
    private readonly CheckBox receiverBox = new();
    private readonly CheckBox startupBox = new();
    private readonly CheckBox unmuteBox = new();
    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings current)
    {
        Result = new AppSettings
        {
            Receivers = current.Receivers.ToList(), Port = current.Port, SharedSecret = current.SharedSecret,
            ReceiverEnabled = current.ReceiverEnabled, StartWithWindows = current.StartWithWindows,
            TemporarilyUnmuteForDoorbell = current.TemporarilyUnmuteForDoorbell
        };

        Text = "LAN Doorbell Pro — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(610, 430);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        receiversBox.Multiline = true;
        receiversBox.ScrollBars = ScrollBars.Vertical;
        receiversBox.AcceptsReturn = true;
        receiversBox.Text = string.Join(Environment.NewLine, current.Receivers);
        portBox.Minimum = 1024; portBox.Maximum = 65535; portBox.Value = Math.Clamp(current.Port, 1024, 65535);
        secretBox.Text = current.SharedSecret;
        receiverBox.Text = "Enable receiver on this PC"; receiverBox.Checked = current.ReceiverEnabled;
        startupBox.Text = "Start automatically with Windows"; startupBox.Checked = current.StartWithWindows;
        unmuteBox.Text = "Temporarily unmute Windows for doorbell"; unmuteBox.Checked = current.TemporarilyUnmuteForDoorbell;

        AddRow(layout, 0, "Receiver computer names / IPs:", receiversBox);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        receiversBox.Height = 105;
        AddRow(layout, 1, "TCP port:", portBox);
        AddRow(layout, 2, "Shared secret:", secretBox);
        layout.Controls.Add(receiverBox, 1, 3);
        layout.Controls.Add(startupBox, 1, 4);
        layout.Controls.Add(unmuteBox, 1, 5);
        layout.Controls.Add(new Label {
            Text = "Enter one receiver per line, e.g. RECEIVER-PC or 192.168.1.4. On a receiver PC, enable receiver above. All receiver PCs must use the same port and shared secret.",
            AutoSize = true, MaximumSize = new Size(370, 0)
        }, 1, 6);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 95 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 95 };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 7); layout.SetColumnSpan(buttons, 2);
        AcceptButton = save; CancelButton = cancel;

        save.Click += (_, _) =>
        {
            var hosts = receiversBox.Text.Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0 && !s.Contains(' ')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (hosts.Count == 0)
            {
                MessageBox.Show("Enter at least one receiver computer name or IPv4 address.", "LAN Doorbell Pro");
                DialogResult = DialogResult.None; return;
            }
            if (string.IsNullOrWhiteSpace(secretBox.Text) || secretBox.Text.Contains('\n') || secretBox.Text.Contains('\r'))
            {
                MessageBox.Show("Enter a shared secret without line breaks.", "LAN Doorbell Pro");
                DialogResult = DialogResult.None; return;
            }
            Result.Receivers = hosts;
            Result.Port = (int)portBox.Value;
            Result.SharedSecret = secretBox.Text;
            Result.ReceiverEnabled = receiverBox.Checked;
            Result.StartWithWindows = startupBox.Checked;
            Result.TemporarilyUnmuteForDoorbell = unmuteBox.Checked;
        };
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        control.Dock = DockStyle.Fill;
        layout.Controls.Add(control, 1, row);
    }
}
