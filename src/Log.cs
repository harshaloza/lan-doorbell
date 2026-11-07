namespace LanDoorbell;

/// <summary>
/// Small thread-safe file log at %AppData%\LanDoorbell\landoorbell.log.
/// Rolls over to landoorbell.old.log at about 1 MB. Logging never throws.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object gate = new();

    public static string FilePath { get; } = Path.Combine(AppInfo.DataFolder, "landoorbell.log");

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) +
                          $" [{level}] {message}";
            if (ex != null)
                line += level == "ERROR" ? Environment.NewLine + ex : $" ({ex.GetType().Name}: {ex.Message})";

            lock (gate)
            {
                AppInfo.EnsureDataFolder();
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(AppInfo.DataFolder, "landoorbell.old.log"), overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
