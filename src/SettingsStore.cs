using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace LanDoorbell;

/// <summary>Loads and saves settings.json, including migration from version 1 files.</summary>
internal static class SettingsStore
{
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private static readonly byte[] dpapiEntropy = Encoding.UTF8.GetBytes("LanDoorbellPro.SharedSecret.v2");

    public static string FilePath { get; } = Path.Combine(AppInfo.DataFolder, "settings.json");

    public static AppSettings Load(out bool firstRun)
    {
        firstRun = !File.Exists(FilePath);
        if (firstRun) return new AppSettings();

        try
        {
            string json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, jsonOptions) ?? new AppSettings();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Version 1 files had no SettingsVersion, stored hosts in "Receivers" (or the even older
            // "ReceiverHost"/"ReceiverIp") and kept the secret as plain text in "SharedSecret".
            bool isVersion1 = !root.TryGetProperty(nameof(AppSettings.SettingsVersion), out _);
            if (isVersion1)
            {
                MigrateVersion1(settings, root);
                settings.SettingsVersion = AppSettings.CurrentVersion;
                TrySave(settings);
                Log.Info("Migrated settings from version 1 (secret is now encrypted).");
            }
            else if (!string.IsNullOrEmpty(settings.ProtectedSecret))
            {
                settings.SharedSecret = Unprotect(settings.ProtectedSecret);
            }

            Normalize(settings);
            return settings;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings.json; starting with defaults", ex);
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { /* best effort */ }
            return new AppSettings();
        }
    }

    private static void MigrateVersion1(AppSettings settings, JsonElement root)
    {
        settings.Targets.Clear();
        if (root.TryGetProperty("Receivers", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                AddLegacyHost(settings, item);
        AddLegacyHost(settings, root.TryGetProperty("ReceiverHost", out var host) ? host : default);
        AddLegacyHost(settings, root.TryGetProperty("ReceiverIp", out var ip) ? ip : default);
        // "RECEIVER-PC" was only an example placeholder in version 1.
        settings.Targets.RemoveAll(t => t.Host.Equals("RECEIVER-PC", StringComparison.OrdinalIgnoreCase));

        if (root.TryGetProperty("SharedSecret", out var secret) && secret.ValueKind == JsonValueKind.String)
            settings.SharedSecret = secret.GetString() ?? "";
    }

    private static void AddLegacyHost(AppSettings settings, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return;
        string value = element.GetString() ?? "";
        if (ReceiverEntry.TryParse(value, out var entry, out _) &&
            !settings.Targets.Any(t => t.Host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase)))
            settings.Targets.Add(entry);
    }

    private static void Normalize(AppSettings s)
    {
        s.Targets = s.Targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Host))
            .GroupBy(t => t.Host.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        s.Port = Math.Clamp(s.Port, 1024, 65535);
        s.SoundVolumePercent = Math.Clamp(s.SoundVolumePercent, 0, 100);
        s.MinimumVolumePercent = Math.Clamp(s.MinimumVolumePercent, 5, 100);
        s.CooldownSeconds = Math.Clamp(s.CooldownSeconds, 0, 600);
        if (string.IsNullOrWhiteSpace(s.DisplayName)) s.DisplayName = Environment.MachineName;
    }

    /// <summary>Writes atomically (temp file + rename). Throws on failure.</summary>
    public static void Save(AppSettings settings)
    {
        settings.SettingsVersion = AppSettings.CurrentVersion;
        settings.ProtectedSecret = string.IsNullOrEmpty(settings.SharedSecret) ? null : Protect(settings.SharedSecret);

        AppInfo.EnsureDataFolder();
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, jsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static bool TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            return false;
        }
    }

    private static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), dpapiEntropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string protectedSecret)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), dpapiEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            // Happens if settings.json was copied from another user or PC. The user must re-enter it.
            Log.Warn("Could not decrypt the shared secret; it must be entered again", ex);
            return "";
        }
    }
}

/// <summary>The HKCU Run entry that starts the app with Windows.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanDoorbell";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the start-with-Windows registry entry", ex);
        }
    }
}
