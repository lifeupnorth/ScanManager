using System.Text.Json;
using System.Text.Json.Serialization;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;

namespace ScanManager.Core.Settings;

/// <summary>User preferences remembered between sessions.</summary>
public sealed class AppSettings
{
    public string? LastDeviceId { get; set; }

    /// <summary>The source last used; selected at startup when there is no <see cref="DefaultSource"/>.</summary>
    public ScanSource Source { get; set; } = ScanSource.Flatbed;

    /// <summary>Source to select at startup and when switching scanners; null means "the one used last".</summary>
    public ScanSource? DefaultSource { get; set; }

    public ColorMode ColorMode { get; set; } = ColorMode.Color;
    public int Dpi { get; set; } = 300;

    /// <summary>Name of the selected paper-size preset, or null for a custom area.</summary>
    public string? PaperSizeName { get; set; }

    public ScanArea? CustomArea { get; set; }
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int BlackWhiteThreshold { get; set; } = 160;
    public bool SkipBlankPages { get; set; }
    public int JpegQuality { get; set; } = 85;
    public string? LastSaveFolder { get; set; }
    public int LastSaveFormatIndex { get; set; } = 1;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Never throws on bad files.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScanManager", "settings.json"));

    public string FilePath { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file just means defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to persist preferences must not take the app down.
        }
    }
}
