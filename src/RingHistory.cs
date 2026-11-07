using System.Text.Json;

namespace LanDoorbell;

internal sealed record HistoryEntry(DateTime Time, string Direction, string Computer, string Result);

/// <summary>Ring history kept as JSON lines in %AppData%\LanDoorbell\history.jsonl (last 1000 entries).</summary>
internal static class RingHistory
{
    public const string Incoming = "Received";
    public const string Outgoing = "Sent";
    private const int KeepEntries = 1000;
    private static readonly object gate = new();
    private static int appendsSinceTrim = int.MaxValue / 2;   // trim once soon after start

    public static string FilePath { get; } = Path.Combine(AppInfo.DataFolder, "history.jsonl");

    public static event Action? Changed;

    public static void Add(string direction, string computer, string result)
    {
        try
        {
            string line = JsonSerializer.Serialize(new HistoryEntry(DateTime.Now, direction, computer, result));
            lock (gate)
            {
                AppInfo.EnsureDataFolder();
                File.AppendAllText(FilePath, line + Environment.NewLine);
                if (++appendsSinceTrim >= 100) TrimLocked();
            }
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write ring history", ex);
        }
    }

    /// <summary>Newest first.</summary>
    public static List<HistoryEntry> ReadRecent(int max = 500)
    {
        try
        {
            string[] lines;
            lock (gate)
            {
                if (!File.Exists(FilePath)) return new();
                lines = File.ReadAllLines(FilePath);
            }
            var result = new List<HistoryEntry>();
            for (int i = lines.Length - 1; i >= 0 && result.Count < max; i--)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(lines[i]);
                    if (entry != null) result.Add(entry);
                }
                catch (JsonException) { /* skip a damaged line */ }
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read ring history", ex);
            return new();
        }
    }

    public static void Clear()
    {
        try
        {
            lock (gate) File.Delete(FilePath);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not clear ring history", ex);
        }
    }

    private static void TrimLocked()
    {
        appendsSinceTrim = 0;
        var lines = File.ReadAllLines(FilePath);
        if (lines.Length > KeepEntries)
            File.WriteAllLines(FilePath, lines[^KeepEntries..]);
    }
}
