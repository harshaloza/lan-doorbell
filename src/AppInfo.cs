using System.Reflection;

namespace LanDoorbell;

/// <summary>App-wide constants, paths and embedded resources.</summary>
internal static class AppInfo
{
    public const string Name = "LAN Doorbell Pro";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// %AppData%\LanDoorbell — settings, history and logs live here.
    /// Set the LANDOORBELL_DATA_DIR environment variable to use another folder (portable use, testing).
    /// </summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("LANDOORBELL_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanDoorbell");

    public static void EnsureDataFolder() => Directory.CreateDirectory(DataFolder);

    public static Stream OpenResource(string logicalName) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
        ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing.");

    public static byte[] ReadResource(string logicalName)
    {
        using var stream = OpenResource(logicalName);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Loads the app icon from inside the EXE, so no .ico file is needed beside it.</summary>
    public static Icon LoadIcon(Size size)
    {
        try
        {
            using var stream = OpenResource("LanDoorbell.icon.ico");
            return new Icon(stream, size);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load embedded icon", ex);
            return (Icon)SystemIcons.Information.Clone();
        }
    }

    public static Image? LoadLogo()
    {
        try
        {
            // Image.FromStream needs the stream to stay open, so give it its own copy.
            return Image.FromStream(new MemoryStream(ReadResource("LanDoorbell.logo.png")));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load embedded logo", ex);
            return null;
        }
    }
}
