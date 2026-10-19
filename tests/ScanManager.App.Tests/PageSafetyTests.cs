using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanManager.App.Imaging;
using ScanManager.App.Services;
using ScanManager.App.ViewModels;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Core.Settings;

namespace ScanManager.App.Tests;

/// <summary>The places where the user's pages are written, kept or thrown away.</summary>
public sealed class PageSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScanManagerAppTests", Guid.NewGuid().ToString("N"));

    public PageSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] PngBytes()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ImageTools.Create(40, 40, 300, PixelFormats.Gray8, new byte[40 * 40], 40)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private string WritePage()
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, PngBytes());
        return path;
    }

    [Fact]
    public void Cleanup_keeps_old_sessions_of_running_windows()
    {
        string live = Path.Combine(_dir, $"session-20260101-000000-{Environment.ProcessId}");
        string dead = Path.Combine(_dir, $"session-20260101-000000-{int.MaxValue}");
        foreach (var session in new[] { live, dead })
        {
            Directory.CreateDirectory(session);
            Directory.SetLastWriteTime(session, DateTime.Now.AddDays(-3));
        }

        PageStore.DeleteStaleSessions(_dir);

        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(dead));
    }

    [Fact]
    public void Saving_pages_as_images_never_replaces_numbered_files()
    {
        string[] pages = [WritePage(), WritePage()];
        string target = Path.Combine(_dir, "receipts.png");

        var written = DocumentExporter.Save(pages, target, ExportFormat.Png, 85);
        Assert.Equal([Path.Combine(_dir, "receipts_001.png"), Path.Combine(_dir, "receipts_002.png")], written);

        var ex = Assert.Throws<IOException>(() => DocumentExporter.Save(pages, target, ExportFormat.Png, 85));
        Assert.Contains("receipts_001.png", ex.Message);
    }

    [Fact]
    public async Task A_page_that_cannot_be_processed_stops_the_scan_and_keeps_earlier_pages()
    {
        var backend = new BrokenPageBackend(PngBytes());
        var dialogs = new RecordingDialogs();
        using var vm = new MainViewModel(new ScannerRegistry([backend]), new PageStore(), dialogs,
            new SettingsStore(Path.Combine(_dir, "settings.json")));
        await vm.InitializeAsync();

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Single(vm.Pages);
        Assert.Single(dialogs.Errors);
        Assert.True(backend.Token.IsCancellationRequested); // a backend scanning ahead (WIA) must stop feeding
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    /// <summary>A feeder whose second page is not an image.</summary>
    private sealed class BrokenPageBackend(byte[] goodPage) : IScannerBackend
    {
        public CancellationToken Token { get; private set; }

        public string BackendId => "fake";

        public Task<IReadOnlyList<ScannerInfo>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>([new ScannerInfo("dev", "dev", BackendId)]);

        public Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(new ScannerCapabilities(
                [new SourceCapabilities(ScanSource.Feeder, new SizeInches(8.5, 14), [300], [ColorMode.Color], null, null)]));

        public async IAsyncEnumerable<ScannedImage> ScanAsync(string deviceId, ScanSettings settings,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Token = ct;
            await Task.Yield();
            yield return new ScannedImage(goodPage, 300, 1, ColorMode.Color, true, true);
            yield return new ScannedImage([1, 2, 3], 300, 2, ColorMode.Color, true, true);
        }
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public List<string> Errors { get; } = [];

        public Preferences? EditPreferences(Preferences current) => null;
        public SaveTarget? AskSaveTarget(string defaultFileName, string? initialFolder, ExportFormat defaultFormat) => null;
        public IReadOnlyList<string>? AskImportFiles(string? initialFolder) => null;
        public bool Confirm(string message, string title) => true;
        public void ShowError(string message, string title = "ScanManager") => Errors.Add(message);
        public bool Print(IReadOnlyList<string> pageFiles, string jobName) => false;
    }
}
