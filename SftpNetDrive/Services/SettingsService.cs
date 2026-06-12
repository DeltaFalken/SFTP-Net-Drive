using System.IO;
using System.Text.Json;

namespace SftpNetDrive.Services;

public sealed class AppSettings
{
    public bool AutoStart { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
}

public static class SettingsService
{
    private static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SftpNetDrive", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static AppSettings? _current;

    public static bool HasSavedSettings => File.Exists(DataPath);

    public static AppSettings Load()
    {
        if (_current is not null) return _current;

        if (!File.Exists(DataPath))
        {
            _current = new AppSettings();
            return _current;
        }

        try
        {
            var json = File.ReadAllText(DataPath);
            _current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            _current = new AppSettings();
        }

        return _current;
    }

    public static void Save()
    {
        var settings = Load();
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllText(DataPath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
