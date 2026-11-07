namespace LanDoorbell;

/// <summary>The tray app: owns the settings, the receiver, the floating button and all windows.</summary>
internal sealed class DoorbellContext : ApplicationContext
{
    private readonly SynchronizationContext ui;
    private readonly SoundService sound = new();
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ContextMenuStrip floatingMenu = new();
    private readonly ToolStripMenuItem ringOneTray = new("Ring one PC");
    private readonly ToolStripMenuItem ringOneFloating = new("Ring one PC");
    private readonly ToolStripMenuItem dndItem = new("Do not disturb");
    private readonly ToolStripMenuItem receiverItem = new("Enable receiver on this PC");
    private readonly ToolStripMenuItem floatingItem = new("Show floating Ring button");
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 60_000 };
    private readonly Dictionary<string, AlertForm> alerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly RegisteredWaitHandle activateRegistration;

    // Read from the receiver's thread for the do-not-disturb check, so always replace, never half-update.
    private volatile AppSettings settings;
    private byte[] key;
    private RingServer? server;
    private FloatingButtonForm? floating;
    private SettingsForm? openSettings;
    private HistoryForm? openHistory;
    private bool ringing;
    private bool exiting;

    public DoorbellContext(EventWaitHandle activateSignal)
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;

        settings = SettingsStore.Load(out bool firstRun);
        key = Protocol.DeriveKey(settings.SharedSecret);

        BuildMenus();
        tray = new NotifyIcon
        {
            Icon = AppInfo.LoadIcon(SystemInformation.SmallIconSize),
            Text = AppInfo.Name,
            ContextMenuStrip = trayMenu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowSettings();

        StartupRegistration.Apply(settings.StartWithWindows);   // also fixes the path if the EXE moved
        if (settings.ReceiverEnabled) StartReceiver();
        if (settings.FloatingButtonVisible) ShowFloatingButton();
        UpdateStatus();

        statusTimer.Tick += (_, _) => UpdateStatus();   // keeps the scheduled do-not-disturb state current
        statusTimer.Start();

        // A second copy of the app signals this event instead of starting; we open Settings.
        activateRegistration = ThreadPool.RegisterWaitForSingleObject(
            activateSignal, (_, _) => ui.Post(_ => ShowSettings(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        if (firstRun || !settings.IsSecretUsable(out _))
        {
            ui.Post(_ =>
            {
                tray.ShowBalloonTip(4000, AppInfo.Name, "Welcome! Add the PCs to ring and a shared secret to get started.", ToolTipIcon.Info);
                ShowSettings();
            }, null);
        }
    }

    // ------------------------------------------------------------------ menus

    private void BuildMenus()
    {
        var ringAll = new ToolStripMenuItem("Ring all PCs", null, async (_, _) => await RingAsync(settings.Targets))
        {
            Font = new Font(trayMenu.Font, FontStyle.Bold),
        };
        dndItem.Click += (_, _) => ToggleDoNotDisturb();
        receiverItem.Click += (_, _) => ToggleReceiver();
        floatingItem.Click += (_, _) => ToggleFloatingButton();

        trayMenu.Items.AddRange(new ToolStripItem[]
        {
            ringAll,
            ringOneTray,
            new ToolStripSeparator(),
            dndItem,
            receiverItem,
            floatingItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Test doorbell sound", null, (_, _) => sound.Play(SoundService.Options.From(settings))),
            new ToolStripMenuItem("Ring history…", null, (_, _) => ShowHistory()),
            new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        });

        floatingMenu.Items.AddRange(new ToolStripItem[]
        {
            ringOneFloating,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Hide this button", null, (_, _) => SetFloatingVisible(false)),
            new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()),
        });
    }

    private void RebuildRingOneMenu(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        foreach (var target in settings.Targets)
        {
            var t = target;
            parent.DropDownItems.Add(new ToolStripMenuItem(t.Display, null, async (_, _) => await RingAsync(new[] { t })));
        }
        if (settings.Targets.Count == 0)
            parent.DropDownItems.Add(new ToolStripMenuItem("(no PCs set up — open Settings)") { Enabled = false });
    }

    private void UpdateStatus()
    {
        if (exiting) return;
        var s = settings;
        RebuildRingOneMenu(ringOneTray);
        RebuildRingOneMenu(ringOneFloating);

        bool scheduled = s.IsScheduledDndActive(DateTime.Now);
        dndItem.Checked = s.DoNotDisturb || scheduled;
        dndItem.Text = scheduled && !s.DoNotDisturb ? "Do not disturb (scheduled now)" : "Do not disturb";
        receiverItem.Checked = server != null;
        floatingItem.Checked = floating is { IsDisposed: false, Visible: true };

        string status = server != null
            ? s.IsDndActive(DateTime.Now) ? "receiver on, do not disturb" : $"receiver on, port {server.Port}"
            : s.ReceiverEnabled ? "receiver NOT running — see Settings" : "sending only";
        string text = $"{AppInfo.Name} ({status})";
        tray.Text = text.Length > 127 ? text[..127] : text;
    }

    // ------------------------------------------------------------------ settings

    private void ShowSettings()
    {
        if (exiting) return;
        if (openSettings != null)
        {
            openSettings.Activate();   // only one Settings window at a time
            return;
        }
        using var form = new SettingsForm(settings, sound, ShowHistory);
        openSettings = form;
        try
        {
            if (form.ShowDialog() == DialogResult.OK) ApplySettings(form.Result);
        }
        finally
        {
            openSettings = null;
        }
    }

    private void ApplySettings(AppSettings updated)
    {
        var old = settings;
        // The button may have been dragged while Settings was open; keep its latest position.
        updated.FloatingButtonX = old.FloatingButtonX;
        updated.FloatingButtonY = old.FloatingButtonY;
        if (updated.SharedSecret != old.SharedSecret) key = Protocol.DeriveKey(updated.SharedSecret);
        settings = updated;
        Save();
        StartupRegistration.Apply(updated.StartWithWindows);

        bool receiverChanged = updated.Port != old.Port || updated.SharedSecret != old.SharedSecret ||
                               updated.CooldownSeconds != old.CooldownSeconds;
        if (!updated.ReceiverEnabled) StopReceiver();
        else if (server == null || receiverChanged) StartReceiver();

        SetFloatingVisible(updated.FloatingButtonVisible, save: false);
        UpdateStatus();
    }

    private void Save()
    {
        try
        {
            SettingsStore.Save(settings);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            MessageBox.Show($"Your settings could not be saved:\n\n{ex.Message}\n\nFile: {SettingsStore.FilePath}",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------------ receiver

    private bool StartReceiver()
    {
        StopReceiver();
        var s = settings;
        if (!s.IsSecretUsable(out string problem))
        {
            tray.ShowBalloonTip(5000, AppInfo.Name, "Receiver not started. " + problem, ToolTipIcon.Warning);
            UpdateStatus();
            return false;
        }

        var newServer = new RingServer(s.Port, key, s.CooldownSeconds, () => settings.IsDndActive(DateTime.Now));
        newServer.RingReceived += ring => ui.Post(_ => OnRingReceived(ring), null);
        newServer.RingBlocked += (name, address, reason) =>
            RingHistory.Add(RingHistory.Incoming, $"{name} ({address})", reason);
        try
        {
            newServer.Start();
            server = newServer;
            Log.Info($"Receiver listening on port {s.Port}");
            UpdateStatus();
            return true;
        }
        catch (Exception ex)
        {
            newServer.Dispose();
            string message = $"Could not listen on port {s.Port}: {ex.Message}";
            Log.Error(message, ex);
            // Keep ReceiverEnabled as the user set it; just report the problem (and don't overwrite it with another balloon).
            tray.ShowBalloonTip(6000, AppInfo.Name, message + "\nIs another program using this port? Change it in Settings.", ToolTipIcon.Error);
            UpdateStatus();
            return false;
        }
    }

    private void StopReceiver()
    {
        server?.Dispose();
        server = null;
        UpdateStatus();
    }

    private void ToggleReceiver()
    {
        bool enable = server == null;
        settings.ReceiverEnabled = enable;
        Save();
        if (enable)
        {
            if (StartReceiver())
                tray.ShowBalloonTip(2500, AppInfo.Name, $"Receiver on. Listening on port {settings.Port}.", ToolTipIcon.Info);
        }
        else
        {
            StopReceiver();
            tray.ShowBalloonTip(2500, AppInfo.Name, "Receiver off.", ToolTipIcon.Info);
        }
    }

    private void ToggleDoNotDisturb()
    {
        var s = settings;
        if (s.IsScheduledDndActive(DateTime.Now) && !s.DoNotDisturb)
        {
            MessageBox.Show("Do not disturb is on because of its daily schedule.\nChange the schedule in Settings → Receive rings.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        s.DoNotDisturb = !s.DoNotDisturb;
        Save();
        UpdateStatus();
        tray.ShowBalloonTip(2000, AppInfo.Name, s.DoNotDisturb ? "Do not disturb is on. Rings will be refused." : "Do not disturb is off.", ToolTipIcon.Info);
    }

    private void OnRingReceived(IncomingRing ring)
    {
        if (exiting)
        {
            ring.Reply.TrySetResult(null);
            return;
        }

        var s = settings;
        RingHistory.Add(RingHistory.Incoming, $"{ring.SenderName} ({ring.RemoteAddress})", "Rang this PC");
        sound.Play(SoundService.Options.From(s));
        tray.ShowBalloonTip(5000, "🔔 Doorbell", $"{ring.SenderName} is ringing.", ToolTipIcon.Info);

        if (!s.ShowAlertWindow)
        {
            ring.Reply.TrySetResult(null);
            return;
        }

        string alertKey = ring.SenderName + "|" + ring.RemoteAddress;
        if (!alerts.TryGetValue(alertKey, out var alert) || alert.IsDisposed)
        {
            alert = new AlertForm();
            alert.Answered += (sender, reply) =>
                RingHistory.Add(RingHistory.Incoming, $"{sender} ({ring.RemoteAddress})", "You replied: " + reply);
            alert.FormClosed += (_, _) => alerts.Remove(alertKey);
            alerts[alertKey] = alert;
        }
        alert.AddRing(ring);
    }

    // ------------------------------------------------------------------ sending

    private async Task RingAsync(IReadOnlyList<ReceiverEntry> targets)
    {
        if (ringing)
        {
            tray.ShowBalloonTip(1500, AppInfo.Name, "Still ringing — please wait.", ToolTipIcon.Info);
            return;
        }
        var s = settings;
        if (targets.Count == 0)
        {
            MessageBox.Show("Add at least one PC to ring in Settings.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowSettings();
            return;
        }
        if (!s.IsSecretUsable(out string problem))
        {
            MessageBox.Show(problem, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowSettings();
            return;
        }

        RingResult[] results;
        ringing = true;
        try
        {
            byte[] k = key;
            results = await Task.WhenAll(targets.Select(t => RingClient.RingAsync(t, s.Port, k, s.DisplayName)));
        }
        finally
        {
            ringing = false;
        }

        foreach (var r in results)
        {
            RingHistory.Add(RingHistory.Outgoing, r.Target.Display, r.Delivered ? "Rang" : $"{r.Status}: {r.Detail}");
            if (r.Reply != null) _ = ShowReplyAsync(r);
        }

        int delivered = results.Count(r => r.Delivered);
        string summary = results.Length == 1
            ? $"{results[0].Target.Label}: {(results[0].Delivered ? "ring sent" : results[0].Detail)}"
            : $"Rang {delivered} of {results.Length} PCs." + string.Concat(results.Where(r => !r.Delivered && !r.IsFailure)
                .Select(r => $"\n{r.Target.Label}: {r.Detail}"));
        tray.ShowBalloonTip(3000, AppInfo.Name, summary, delivered > 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);

        var failures = results.Where(r => r.IsFailure).ToList();
        if (failures.Count > 0)
        {
            MessageBox.Show(
                "Could not ring:\n\n" + string.Join("\n", failures.Select(r => $"• {r.Target.Display}: {r.Detail}")) +
                "\n\nTip: Settings → Ring other PCs → Test connection checks each PC.",
                "LAN Doorbell — not delivered", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ShowReplyAsync(RingResult result)
    {
        string? reply = await result.Reply!;
        if (reply == null || exiting) return;
        RingHistory.Add(RingHistory.Incoming, result.Target.Display, "Replied: " + reply);
        tray.ShowBalloonTip(6000, $"💬 {result.Target.Label}", reply, ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------ floating button & history

    private void ShowFloatingButton()
    {
        if (floating == null || floating.IsDisposed)
        {
            Point? saved = settings.FloatingButtonX is int x && settings.FloatingButtonY is int y ? new Point(x, y) : null;
            floating = new FloatingButtonForm(() => RingAsync(settings.Targets), floatingMenu, saved);
            floating.PositionCommitted += p =>
            {
                settings.FloatingButtonX = p.X;
                settings.FloatingButtonY = p.Y;
                SettingsStore.TrySave(settings);
            };
        }
        floating.Show();
        UpdateStatus();
    }

    private void SetFloatingVisible(bool visible, bool save = true)
    {
        if (visible) ShowFloatingButton();
        else floating?.Hide();
        if (save)
        {
            settings.FloatingButtonVisible = visible;
            Save();
        }
        UpdateStatus();
    }

    private void ToggleFloatingButton() => SetFloatingVisible(!(floating is { IsDisposed: false, Visible: true }));

    private void ShowHistory()
    {
        if (openHistory == null || openHistory.IsDisposed)
        {
            openHistory = new HistoryForm();
            openHistory.FormClosed += (_, _) => openHistory = null;
        }
        openHistory.Show();
        openHistory.Activate();
    }

    // ------------------------------------------------------------------ exit

    private void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        Log.Info("Exiting");
        activateRegistration.Unregister(null);
        statusTimer.Dispose();
        StopReceiver();
        foreach (var alert in alerts.Values.ToList()) alert.Close();
        openHistory?.Close();
        floating?.Close();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        if (!exiting)
        {
            exiting = true;
            server?.Dispose();
            tray.Visible = false;
        }
        base.ExitThreadCore();
    }
}
