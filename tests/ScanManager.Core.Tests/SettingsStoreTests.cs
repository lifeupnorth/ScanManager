using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Core.Settings;

namespace ScanManager.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScanManagerTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Saved_settings_load_back()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings
        {
            LastDeviceId = "dev-1",
            Source = ScanSource.Duplex,
            ColorMode = ColorMode.BlackAndWhite,
            Dpi = 600,
            CustomArea = new ScanArea(1, 2, 3, 4),
            SkipBlankPages = true,
        });

        var loaded = store.Load();
        Assert.Equal("dev-1", loaded.LastDeviceId);
        Assert.Equal(ScanSource.Duplex, loaded.Source);
        Assert.Equal(ColorMode.BlackAndWhite, loaded.ColorMode);
        Assert.Equal(600, loaded.Dpi);
        Assert.Equal(new ScanArea(1, 2, 3, 4), loaded.CustomArea);
        Assert.True(loaded.SkipBlankPages);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var loaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal(300, loaded.Dpi);
        Assert.Equal(ScanSource.Flatbed, loaded.Source);
    }

    [Fact]
    public void Corrupt_file_gives_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ not json");
        var loaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal(300, loaded.Dpi);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
