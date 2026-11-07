using System.Text.Json.Serialization;

namespace LanDoorbell;

/// <summary>One PC this computer can ring. Written in Settings as "Name = host" or just "host".</summary>
internal sealed class ReceiverEntry
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";

    /// <summary>Short label for menus: the friendly name, or the host if there is none.</summary>
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>Label plus host, for results and history.</summary>
    [JsonIgnore]
    public string Display =>
        string.IsNullOrWhiteSpace(Name) || Name.Equals(Host, StringComparison.OrdinalIgnoreCase) ? Host : $"{Name} ({Host})";

    /// <summary>The line format used in the Settings text box.</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Host : $"{Name} = {Host}";

    public static bool TryParse(string line, out ReceiverEntry entry, out string error)
    {
        entry = new ReceiverEntry();
        error = "";
        string name = "";
        string host = line.Trim();

        int eq = host.IndexOf('=');
        if (eq >= 0)
        {
            name = host[..eq].Trim();
            host = host[(eq + 1)..].Trim();
        }

        if (host.Length == 0)
        {
            error = "the computer name or IP address is missing";
            return false;
        }
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = $"\"{host}\" is not a valid computer name or IP address (no spaces allowed)";
            return false;
        }
        if (name.Length > 40)
        {
            error = "the friendly name is longer than 40 characters";
            return false;
        }

        entry = new ReceiverEntry { Name = name, Host = host };
        return true;
    }
}

internal sealed class AppSettings
{
    public const int CurrentVersion = 2;
    public const int DefaultPort = 45872;
    public const int MinSecretLength = 8;
    public const string OldDefaultSecret = "CHANGE-THIS-TO-A-LONG-SECRET";

    public int SettingsVersion { get; set; } = CurrentVersion;

    // ---- Sending ----
    public List<ReceiverEntry> Targets { get; set; } = new();
    /// <summary>Name shown on the receiver ("DESIGN-PC is ringing").</summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    // ---- Connection ----
    public int Port { get; set; } = DefaultPort;
    /// <summary>The shared secret, encrypted with Windows DPAPI for the current user.</summary>
    public string? ProtectedSecret { get; set; }
    /// <summary>Plain shared secret, in memory only. Never written to disk.</summary>
    [JsonIgnore]
    public string SharedSecret { get; set; } = "";

    // ---- Receiving ----
    public bool ReceiverEnabled { get; set; }
    public bool ShowAlertWindow { get; set; } = true;
    /// <summary>Custom .wav/.mp3 file, or null for the built-in ding-dong.</summary>
    public string? SoundFile { get; set; }
    public int SoundVolumePercent { get; set; } = 100;
    public bool TemporarilyUnmuteForDoorbell { get; set; }
    public bool RaiseVolumeIfLow { get; set; }
    public int MinimumVolumePercent { get; set; } = 40;
    /// <summary>Rings from the same PC closer together than this are refused.</summary>
    public int CooldownSeconds { get; set; } = 5;

    // ---- Do not disturb ----
    public bool DoNotDisturb { get; set; }
    public bool DndScheduleEnabled { get; set; }
    public string DndFrom { get; set; } = "22:00";
    public string DndTo { get; set; } = "07:00";

    // ---- General ----
    public bool StartWithWindows { get; set; }
    public bool FloatingButtonVisible { get; set; } = true;
    public int? FloatingButtonX { get; set; }
    public int? FloatingButtonY { get; set; }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Targets = Targets.Select(t => new ReceiverEntry { Name = t.Name, Host = t.Host }).ToList();
        return copy;
    }

    public static bool IsSecretUsable(string secret, out string problem)
    {
        problem = "";
        if (string.IsNullOrWhiteSpace(secret))
            problem = "No shared secret is set. Open Settings and set one (the same on every PC).";
        else if (secret == OldDefaultSecret)
            problem = "The shared secret is still the example value. Choose a new one (the same on every PC).";
        else if (secret.Length < MinSecretLength)
            problem = $"The shared secret must be at least {MinSecretLength} characters.";
        else if (secret.Contains('\r') || secret.Contains('\n'))
            problem = "The shared secret cannot contain line breaks.";
        return problem.Length == 0;
    }

    public bool IsSecretUsable(out string problem) => IsSecretUsable(SharedSecret, out problem);

    public bool IsScheduledDndActive(DateTime now)
    {
        if (!DndScheduleEnabled ||
            !TimeOnly.TryParse(DndFrom, out var from) ||
            !TimeOnly.TryParse(DndTo, out var to) ||
            from == to)
            return false;

        var t = TimeOnly.FromDateTime(now);
        return from < to
            ? t >= from && t < to          // e.g. 12:00–13:00
            : t >= from || t < to;         // overnight, e.g. 22:00–07:00
    }

    public bool IsDndActive(DateTime now) => DoNotDisturb || IsScheduledDndActive(now);
}
