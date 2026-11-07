using System.Diagnostics;

namespace LanDoorbell;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings original;
    private readonly SoundService sound;
    private readonly Action showHistory;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private TabPage sendPage = null!, connectionPage = null!, receivePage = null!;

    // Send
    private readonly TextBox receiversBox = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly TextBox displayNameBox = new() { MaxLength = Protocol.MaxNameChars };
    private readonly Button testButton = new() { Text = "Test connection", AutoSize = true };
    private readonly TextBox testResults = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    // Connection
    private readonly NumericUpDown portBox = new() { Minimum = 1024, Maximum = 65535 };
    private readonly TextBox secretBox = new() { UseSystemPasswordChar = true };
    private readonly CheckBox showSecretBox = new() { Text = "Show", AutoSize = true };
    // Receive
    private readonly CheckBox receiverBox = new() { Text = "Enable receiver on this PC (other PCs can ring this one)", AutoSize = true };
    private readonly CheckBox alertBox = new() { Text = "Show a large alert window when someone rings", AutoSize = true };
    private readonly RadioButton builtInSoundRadio = new() { Text = "Built-in ding-dong", AutoSize = true };
    private readonly RadioButton customSoundRadio = new() { Text = "Custom sound:", AutoSize = true };
    private readonly TextBox soundFileBox = new();
    private readonly TrackBar volumeBar = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10 };
    private readonly Label volumeLabel = new() { AutoSize = true };
    private readonly CheckBox unmuteBox = new() { Text = "Temporarily unmute Windows for the doorbell", AutoSize = true };
    private readonly CheckBox raiseBox = new() { Text = "Raise Windows volume to at least", AutoSize = true };
    private readonly NumericUpDown minVolumeBox = new() { Minimum = 5, Maximum = 100, Increment = 5 };
    private readonly NumericUpDown cooldownBox = new() { Minimum = 0, Maximum = 600 };
    private readonly CheckBox dndNowBox = new() { Text = "Do not disturb now", AutoSize = true };
    private readonly CheckBox dndScheduleBox = new() { Text = "Do not disturb every day from", AutoSize = true };
    private readonly DateTimePicker dndFromPicker = TimePicker();
    private readonly DateTimePicker dndToPicker = TimePicker();
    // General
    private readonly CheckBox startupBox = new() { Text = "Start automatically with Windows", AutoSize = true };
    private readonly CheckBox floatingBox = new() { Text = "Show the floating Ring button", AutoSize = true };

    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings current, SoundService sound, Action showHistory)
    {
        original = current;
        Result = current.Clone();
        this.sound = sound;
        this.showHistory = showHistory;

        Text = "Settings — " + AppInfo.Name;
        Icon = AppInfo.LoadIcon(SystemInformation.IconSize);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(D(640), D(560));

        BuildSendTab();
        BuildConnectionTab();
        BuildReceiveTab();
        BuildGeneralTab();
        LoadValues(current);

        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(D(90), 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(D(90), 0), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => OnSave();
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(D(8)) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        Controls.Add(tabs);
        Controls.Add(buttons);
        Shown += (_, _) => Activate();
    }

    private int D(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);

    // ------------------------------------------------------------------ layout helpers

    private TableLayoutPanel AddPage(string title, out TabPage page)
    {
        page = new TabPage(title) { Padding = new Padding(D(10)), UseVisualStyleBackColor = false };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        tabs.TabPages.Add(page);
        return table;
    }

    private void Row(TableLayoutPanel table, string? label, Control control, bool fill = false)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label == null)
        {
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
        }
        else
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, D(7), D(10), 3) }, 0, row);
            table.Controls.Add(control, 1, row);
        }
        if (fill) control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
    }

    /// <param name="wide">True when the note spans both columns.</param>
    private Label Note(string text, bool wide = false) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(D(wide ? 580 : 470), 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 2, 3, D(10)),
    };

    private Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font, FontStyle.Bold),
        Margin = new Padding(3, D(12), 3, D(4)),
    };

    private FlowLayoutPanel Inline(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private Label InlineLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, D(6), 3, 0) };

    private static DateTimePicker TimePicker() => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
    };

    // ------------------------------------------------------------------ tabs

    private void BuildSendTab()
    {
        var t = AddPage("Ring other PCs", out sendPage);
        receiversBox.Height = D(130);
        Row(t, "PCs to ring:", receiversBox, fill: true);
        Row(t, "", Note("One PC per line: a computer name or IP address. Add a friendly name with \"=\", for example:\r\n" +
                        "    Toolroom = 192.168.1.40\r\n    DESIGN-PC"));
        displayNameBox.Width = D(220);
        Row(t, "Your name:", displayNameBox);
        Row(t, "", Note("Shown on the other PC, e.g. \"Reception is ringing\"."));
        testButton.Click += async (_, _) => await TestConnectionAsync();
        Row(t, null, Inline(testButton, InlineLabel("Checks each PC is reachable and uses the same secret. It does not ring.")));
        testResults.Height = D(110);
        Row(t, null, testResults, fill: true);
    }

    private void BuildConnectionTab()
    {
        var t = AddPage("Connection", out connectionPage);
        portBox.Width = D(100);
        Row(t, "TCP port:", portBox);
        secretBox.Width = D(260);
        var generate = new Button { Text = "Generate", AutoSize = true };
        var copy = new Button { Text = "Copy", AutoSize = true };
        showSecretBox.Margin = new Padding(D(6), D(6), 3, 0);
        showSecretBox.CheckedChanged += (_, _) => secretBox.UseSystemPasswordChar = !showSecretBox.Checked;
        generate.Click += (_, _) =>
        {
            if (secretBox.Text.Length > 0 &&
                MessageBox.Show(this, "Replace the current secret with a new random one?\nYou will need to enter the new secret on every PC.",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            secretBox.Text = Protocol.GenerateSecret();
            showSecretBox.Checked = true;
        };
        copy.Click += (_, _) =>
        {
            if (secretBox.Text.Length == 0) return;
            try { Clipboard.SetText(secretBox.Text); }
            catch (Exception ex) { Log.Warn("Could not copy to clipboard", ex); }
        };
        Row(t, "Shared secret:", Inline(secretBox, showSecretBox, generate, copy));
        Row(t, "", Note($"At least {AppSettings.MinSecretLength} characters. Use exactly the same port and secret on every PC.\r\n" +
                        "The secret is stored encrypted on this PC and is never sent over the network."));
        Row(t, null, Heading("Firewall (receiver PCs only)"));
        Row(t, null, Note(wide: true, text: "Each PC that receives rings needs an inbound firewall rule. Open PowerShell as Administrator in the app's folder and run:\r\n" +
                          "    powershell -ExecutionPolicy Bypass -File .\\Install-Firewall-Receiver.ps1 -Port <port>\r\n" +
                          "(Install.ps1 -Receiver does this for you.) The rule only allows your local subnet on Private networks. " +
                          "Never forward this port on your router."));
    }

    private void BuildReceiveTab()
    {
        var t = AddPage("Receive rings", out receivePage);
        Row(t, null, receiverBox);
        Row(t, null, alertBox);

        Row(t, null, Heading("Sound"));
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseSound();
        soundFileBox.Width = D(300);
        soundFileBox.TextChanged += (_, _) => { if (soundFileBox.Text.Length > 0) customSoundRadio.Checked = true; };
        // The two radio buttons sit in different containers, so keep them exclusive by hand.
        builtInSoundRadio.CheckedChanged += (_, _) => { if (builtInSoundRadio.Checked) customSoundRadio.Checked = false; };
        customSoundRadio.CheckedChanged += (_, _) => { if (customSoundRadio.Checked) builtInSoundRadio.Checked = false; };
        Row(t, null, builtInSoundRadio);
        Row(t, null, Inline(customSoundRadio, soundFileBox, browse));

        volumeBar.Width = D(220);
        volumeBar.AutoSize = false;
        volumeBar.Height = D(32);
        volumeBar.ValueChanged += (_, _) => volumeLabel.Text = volumeBar.Value + "%";
        volumeLabel.Margin = new Padding(3, D(6), 3, 0);
        var play = new Button { Text = "▶ Test sound", AutoSize = true };
        play.Click += (_, _) => sound.Play(ReadSoundOptions());
        Row(t, "Volume:", Inline(volumeBar, volumeLabel, play));
        Row(t, null, unmuteBox);
        minVolumeBox.Width = D(60);
        Row(t, null, Inline(raiseBox, minVolumeBox, InlineLabel("% while ringing")));

        Row(t, null, Heading("Interruptions"));
        cooldownBox.Width = D(60);
        Row(t, null, Inline(InlineLabel("Ignore repeat rings from the same PC within"), cooldownBox, InlineLabel("seconds")));
        Row(t, null, dndNowBox);
        dndFromPicker.Width = dndToPicker.Width = D(70);
        Row(t, null, Inline(dndScheduleBox, dndFromPicker, InlineLabel("to"), dndToPicker));
        Row(t, null, Note(wide: true, text: "During do not disturb, rings are refused and the sender is told. You can also switch it from the tray menu."));
    }

    private void BuildGeneralTab()
    {
        var t = AddPage("General", out _);
        Row(t, null, startupBox);
        Row(t, null, floatingBox);
        Row(t, null, Note(wide: true, text: "Floating button: left-click rings everyone, right-drag moves it, right-click opens its menu."));

        var history = new Button { Text = "Ring history…", AutoSize = true };
        history.Click += (_, _) => showHistory();
        var folder = new Button { Text = "Open data folder", AutoSize = true };
        folder.Click += (_, _) => OpenInExplorer(AppInfo.DataFolder);
        Row(t, null, Inline(history, folder));

        var logo = new PictureBox
        {
            Image = AppInfo.LoadLogo(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(D(56), D(56)),
            Margin = new Padding(3, D(20), D(10), 3),
        };
        var about = new Label
        {
            Text = $"{AppInfo.Name} {AppInfo.Version}\r\nRing other PCs on your local network.\r\nSettings, history and logs: {AppInfo.DataFolder}",
            AutoSize = true,
            MaximumSize = new Size(D(500), 0),
            Margin = new Padding(3, D(26), 3, 3),
        };
        Row(t, null, Inline(logo, about));
    }

    // ------------------------------------------------------------------ values

    private void LoadValues(AppSettings s)
    {
        receiversBox.Text = string.Join(Environment.NewLine, s.Targets.Select(r => r.ToString()));
        displayNameBox.Text = s.DisplayName;
        portBox.Value = Math.Clamp(s.Port, 1024, 65535);
        secretBox.Text = s.SharedSecret == AppSettings.OldDefaultSecret ? "" : s.SharedSecret;
        receiverBox.Checked = s.ReceiverEnabled;
        alertBox.Checked = s.ShowAlertWindow;
        soundFileBox.Text = s.SoundFile ?? "";
        builtInSoundRadio.Checked = s.SoundFile == null;
        customSoundRadio.Checked = s.SoundFile != null;
        volumeBar.Value = Math.Clamp(s.SoundVolumePercent, 0, 100);
        volumeLabel.Text = volumeBar.Value + "%";
        unmuteBox.Checked = s.TemporarilyUnmuteForDoorbell;
        raiseBox.Checked = s.RaiseVolumeIfLow;
        minVolumeBox.Value = Math.Clamp(s.MinimumVolumePercent, 5, 100);
        cooldownBox.Value = Math.Clamp(s.CooldownSeconds, 0, 600);
        dndNowBox.Checked = s.DoNotDisturb;
        dndScheduleBox.Checked = s.DndScheduleEnabled;
        dndFromPicker.Value = DateTime.Today + (TimeOnly.TryParse(s.DndFrom, out var from) ? from.ToTimeSpan() : TimeSpan.FromHours(22));
        dndToPicker.Value = DateTime.Today + (TimeOnly.TryParse(s.DndTo, out var to) ? to.ToTimeSpan() : TimeSpan.FromHours(7));
        startupBox.Checked = s.StartWithWindows;
        floatingBox.Checked = s.FloatingButtonVisible;
    }

    private bool TryReadTargets(out List<ReceiverEntry> targets, out string error)
    {
        targets = new List<ReceiverEntry>();
        var problems = new List<string>();
        string[] lines = receiversBox.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!ReceiverEntry.TryParse(line, out var entry, out string why))
                problems.Add($"Line {i + 1}: {why}");
            else if (!targets.Any(t => t.Host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase)))
                targets.Add(entry);
        }
        error = string.Join(Environment.NewLine, problems);
        return problems.Count == 0;
    }

    private SoundService.Options ReadSoundOptions() => new(
        customSoundRadio.Checked && soundFileBox.Text.Trim().Length > 0 ? soundFileBox.Text.Trim() : null,
        volumeBar.Value,
        unmuteBox.Checked,
        raiseBox.Checked,
        (int)minVolumeBox.Value);

    private void OnSave()
    {
        if (!TryReadTargets(out var targets, out string targetErrors))
        {
            Fail(sendPage, "Please fix these PCs:\n\n" + targetErrors);
            return;
        }
        if (targets.Count == 0 && !receiverBox.Checked)
        {
            Fail(sendPage, "Add at least one PC to ring, or enable the receiver on this PC.");
            return;
        }
        if (!AppSettings.IsSecretUsable(secretBox.Text, out string secretProblem))
        {
            Fail(connectionPage, secretProblem);
            return;
        }
        var soundOptions = ReadSoundOptions();
        if (soundOptions.SoundFile != null && !File.Exists(soundOptions.SoundFile))
        {
            Fail(receivePage, "The custom sound file was not found:\n" + soundOptions.SoundFile);
            return;
        }

        var r = original.Clone();
        r.Targets = targets;
        r.DisplayName = string.IsNullOrWhiteSpace(displayNameBox.Text) ? Environment.MachineName : displayNameBox.Text.Trim();
        r.Port = (int)portBox.Value;
        r.SharedSecret = secretBox.Text;
        r.ReceiverEnabled = receiverBox.Checked;
        r.ShowAlertWindow = alertBox.Checked;
        r.SoundFile = soundOptions.SoundFile;
        r.SoundVolumePercent = volumeBar.Value;
        r.TemporarilyUnmuteForDoorbell = unmuteBox.Checked;
        r.RaiseVolumeIfLow = raiseBox.Checked;
        r.MinimumVolumePercent = (int)minVolumeBox.Value;
        r.CooldownSeconds = (int)cooldownBox.Value;
        r.DoNotDisturb = dndNowBox.Checked;
        r.DndScheduleEnabled = dndScheduleBox.Checked;
        r.DndFrom = dndFromPicker.Value.ToString("HH:mm");
        r.DndTo = dndToPicker.Value.ToString("HH:mm");
        r.StartWithWindows = startupBox.Checked;
        r.FloatingButtonVisible = floatingBox.Checked;
        Result = r;
        DialogResult = DialogResult.OK;
    }

    private void Fail(TabPage page, string message)
    {
        tabs.SelectedTab = page;
        MessageBox.Show(this, message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task TestConnectionAsync()
    {
        if (!TryReadTargets(out var targets, out string errors))
        {
            testResults.Text = errors;
            return;
        }
        if (targets.Count == 0)
        {
            testResults.Text = "Add at least one PC above first.";
            return;
        }
        if (!AppSettings.IsSecretUsable(secretBox.Text, out string problem))
        {
            testResults.Text = problem;
            return;
        }

        testButton.Enabled = false;
        testResults.Text = "Testing…";
        try
        {
            int port = (int)portBox.Value;
            string secret = secretBox.Text;
            byte[] key = await Task.Run(() => Protocol.DeriveKey(secret));
            var results = await Task.WhenAll(targets.Select(t => RingClient.TestAsync(t, port, key)));
            if (IsDisposed) return;
            testResults.Text = string.Join(Environment.NewLine,
                results.Select(r => $"{(r.Delivered ? "✔" : "✖")}  {r.Target.Display}: {r.Detail}"));
        }
        finally
        {
            if (!IsDisposed) testButton.Enabled = true;
        }
    }

    private void BrowseSound()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a doorbell sound",
            Filter = "Sound files (*.wav;*.mp3)|*.wav;*.mp3|All files (*.*)|*.*",
        };
        if (File.Exists(soundFileBox.Text)) dialog.FileName = soundFileBox.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            soundFileBox.Text = dialog.FileName;
            customSoundRadio.Checked = true;
        }
    }

    private static void OpenInExplorer(string folder)
    {
        try
        {
            AppInfo.EnsureDataFolder();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open folder", ex);
        }
    }
}
