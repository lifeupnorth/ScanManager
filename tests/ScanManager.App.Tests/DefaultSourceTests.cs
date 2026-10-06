using System.IO;
using System.Runtime.CompilerServices;
using ScanManager.App.Services;
using ScanManager.App.ViewModels;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Core.Settings;
using static ScanManager.Core.Scanning.ScanSource;

namespace ScanManager.App.Tests;

public sealed class DefaultSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScanManagerAppTests", Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;
    private readonly FakeDialogs _dialogs = new();

    public DefaultSourceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SettingsStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<MainViewModel> StartAsync(params FakeScanner[] scanners)
    {
        var vm = new MainViewModel(new ScannerRegistry([new FakeBackend(scanners)]), new PageStore(), _dialogs, _store);
        await vm.InitializeAsync();
        return vm;
    }

    private static FakeScanner Mfp(string id = "mfp") => new(id, [Flatbed, Feeder, Duplex]);

    [Fact]
    public async Task The_default_source_is_selected_at_startup_instead_of_the_last_used_one()
    {
        _store.Save(new AppSettings { Source = Flatbed, DefaultSource = Feeder });
        using var vm = await StartAsync(Mfp());
        Assert.Equal(Feeder, vm.SelectedSource);
    }

    [Fact]
    public async Task Without_a_default_the_last_used_source_is_selected()
    {
        _store.Save(new AppSettings { Source = Duplex });
        using var vm = await StartAsync(Mfp());
        Assert.Equal(Duplex, vm.SelectedSource);
    }

    [Fact]
    public async Task A_scanner_without_duplex_falls_back_to_one_sided_feeding()
    {
        _store.Save(new AppSettings { DefaultSource = Duplex });
        using var vm = await StartAsync(new FakeScanner("simplex", [Flatbed, Feeder]));
        Assert.Equal(Feeder, vm.SelectedSource);
    }

    [Fact]
    public async Task Refreshing_keeps_a_source_picked_by_hand()
    {
        _store.Save(new AppSettings { DefaultSource = Feeder });
        using var vm = await StartAsync(Mfp());

        vm.SelectedSource = Flatbed;
        await vm.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.Equal(Flatbed, vm.SelectedSource);
    }

    [Fact]
    public async Task Switching_scanners_applies_the_default_again()
    {
        _store.Save(new AppSettings { DefaultSource = Feeder });
        using var vm = await StartAsync(Mfp("first"), Mfp("second"));

        vm.SelectedSource = Flatbed;
        vm.SelectedDevice = vm.Devices[1];

        Assert.Equal(Feeder, vm.SelectedSource);
    }

    [Fact]
    public async Task Choosing_a_default_in_settings_selects_it_and_saves_it()
    {
        using var vm = await StartAsync(Mfp());
        Assert.Equal(Flatbed, vm.SelectedSource);

        _dialogs.NextPreferences = new Preferences(Feeder);
        vm.OpenSettingsCommand.Execute(null);

        Assert.Equal(Feeder, vm.SelectedSource);
        Assert.Equal(Feeder, _store.Load().DefaultSource);
    }

    [Fact]
    public async Task Cancelling_settings_changes_nothing()
    {
        using var vm = await StartAsync(Mfp());
        _dialogs.NextPreferences = null;
        vm.OpenSettingsCommand.Execute(null);

        Assert.Equal(Flatbed, vm.SelectedSource);
        Assert.Null(_store.Load().DefaultSource);
    }

    private sealed record FakeScanner(string Id, ScanSource[] Sources);

    private sealed class FakeBackend(FakeScanner[] scanners) : IScannerBackend
    {
        public string BackendId => "fake";

        public Task<IReadOnlyList<ScannerInfo>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>(scanners.Select(s => new ScannerInfo(s.Id, s.Id, BackendId)).ToList());

        public Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(new ScannerCapabilities(scanners.Single(s => s.Id == deviceId).Sources
                .Select(source => new SourceCapabilities(source, new SizeInches(8.5, 14), [300], [ColorMode.Color], null, null))
                .ToList()));

        public async IAsyncEnumerable<ScannedImage> ScanAsync(string deviceId, ScanSettings settings,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public Preferences? NextPreferences { get; set; }

        public Preferences? EditPreferences(Preferences current) => NextPreferences;
        public SaveTarget? AskSaveTarget(string defaultFileName, string? initialFolder, ExportFormat defaultFormat) => null;
        public IReadOnlyList<string>? AskImportFiles(string? initialFolder) => null;
        public bool Confirm(string message, string title) => true;
        public void ShowError(string message, string title = "ScanManager") => throw new InvalidOperationException(message);
        public bool Print(IReadOnlyList<string> pageFiles, string jobName) => false;
    }
}
