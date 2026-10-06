using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanManager.App.Imaging;
using ScanManager.App.Services;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Core.Settings;

namespace ScanManager.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxUndoSteps = 50;
    private const int DefaultThreshold = 160;
    private static readonly SizeInches DefaultBed = new(8.5, 11.69);
    private static readonly int[] DefaultResolutions = [100, 150, 200, 300, 600];

    private readonly ScannerRegistry _registry;
    private readonly PageStore _store;
    private readonly IDialogService _dialogs;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly List<UndoStep> _undo = [];
    private readonly bool _initializing;

    private ScannerCapabilities? _capabilities;
    private BitmapSource? _flatbedPreview;
    private string? _selectedPageImagePath;
    private CancellationTokenSource? _scanCts;
    private int _capabilitiesRequest;
    private bool _syncingArea;

    private enum ScanOutcome { Completed, Cancelled, Failed }

    private sealed record UndoStep(string Name, Action Undo);

    public MainViewModel(ScannerRegistry registry, PageStore store, IDialogService dialogs, SettingsStore settingsStore)
    {
        _registry = registry;
        _store = store;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();

        _initializing = true;
        StatusText = "Starting…";
        SelectedSource = _settings.Source;
        SelectedColorMode = _settings.ColorMode;
        SelectedDpi = _settings.Dpi;
        Brightness = _settings.Brightness;
        Contrast = _settings.Contrast;
        BlackWhiteThreshold = _settings.BlackWhiteThreshold;
        SkipBlankPages = _settings.SkipBlankPages;
        BedSize = new Size(DefaultBed.Width, DefaultBed.Height);
        Area = _settings.CustomArea ?? ScanArea.Full(DefaultBed);
        _initializing = false;

        Pages.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Pages.Count; i++) Pages[i].Number = i + 1;
            OnPropertyChanged(nameof(HasPages));
            OnPropertyChanged(nameof(PageCountText));
            RefreshCommands();
        };

        ApplySourceCapabilities();
    }

    // ---- Collections ----------------------------------------------------------------------

    public ObservableCollection<ScannerInfo> Devices { get; } = [];
    public ObservableCollection<ScanSource> Sources { get; } = [];
    public ObservableCollection<int> Resolutions { get; } = [];
    public IReadOnlyList<ColorMode> ColorModes { get; } = [ColorMode.Color, ColorMode.Grayscale, ColorMode.BlackAndWhite];
    public ObservableCollection<AreaPreset> AreaPresets { get; } = [];
    public ObservableCollection<PageViewModel> Pages { get; } = [];

    // ---- Scan settings --------------------------------------------------------------------

    [ObservableProperty]
    public partial ScannerInfo? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFeederSource), nameof(CanPreview), nameof(ScanAreaHint), nameof(AreaDescription), nameof(PreviewPlaceholder))]
    public partial ScanSource SelectedSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreaDescription))]
    public partial int SelectedDpi { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlackAndWhite))]
    public partial ColorMode SelectedColorMode { get; set; }

    [ObservableProperty]
    public partial AreaPreset? SelectedAreaPreset { get; set; }

    /// <summary>The area to scan, in inches from the top-left of the bed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreaDescription))]
    public partial ScanArea Area { get; set; }

    /// <summary><see cref="Area"/> as fractions of the bed, for the selection frame.</summary>
    [ObservableProperty]
    public partial Rect NormalizedArea { get; set; }

    /// <summary>Scannable size of the current source in inches.</summary>
    [ObservableProperty]
    public partial Size BedSize { get; set; }

    [ObservableProperty]
    public partial int Brightness { get; set; }

    [ObservableProperty]
    public partial int Contrast { get; set; }

    [ObservableProperty]
    public partial int BlackWhiteThreshold { get; set; }

    [ObservableProperty]
    public partial bool SkipBlankPages { get; set; }

    [ObservableProperty]
    public partial ImageSource? PreviewImage { get; set; }

    // ---- Pages ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagePlaceholder))]
    public partial PageViewModel? SelectedPage { get; set; }

    [ObservableProperty]
    public partial BitmapSource? SelectedPageImage { get; set; }

    [ObservableProperty]
    public partial bool IsCropMode { get; set; }

    [ObservableProperty]
    public partial Rect CropSelection { get; set; }

    [ObservableProperty]
    public partial bool HasUnsavedChanges { get; set; }

    // ---- Window state ---------------------------------------------------------------------

    /// <summary>0 = scan area / preview, 1 = selected page.</summary>
    [ObservableProperty]
    public partial int ViewIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    public bool IsIdle => !IsBusy;
    public bool IsFeederSource => SelectedSource != ScanSource.Flatbed;
    public bool IsBlackAndWhite => SelectedColorMode == ColorMode.BlackAndWhite;
    public bool HasPages => Pages.Count > 0;
    public bool CanPreview => SelectedDevice is not null && SelectedSource == ScanSource.Flatbed;
    public string UndoDescription => _undo.Count > 0 ? $"Undo {_undo[^1].Name}" : "Nothing to undo";

    public string PageCountText => Pages.Count switch
    {
        0 => "No pages",
        1 => "1 page",
        int n => $"{n} pages",
    };

    public string ScanAreaHint => IsFeederSource
        ? "Pages from the document feeder are scanned at the selected paper size. Preview is only available on the flatbed."
        : "Click Preview to see what's on the glass, then drag the frame to choose what to scan.";

    public string PreviewPlaceholder => IsFeederSource
        ? "The frame shows the paper size that will be scanned from the document feeder."
        : "No preview yet.\nPut the document face down on the glass and click Preview.";

    public string PagePlaceholder => SelectedPage is null ? "Select a page in the document list." : "Loading page…";

    public string AreaDescription
    {
        get
        {
            var a = Area;
            var px = a.ToPixels(Math.Max(1, SelectedDpi));
            string size = $"{a.Width:0.00} × {a.Height:0.00} in ({a.Width * SizeInches.MillimetersPerInch:0} × {a.Height * SizeInches.MillimetersPerInch:0} mm)";
            string position = IsFeederSource ? "" : $", {a.X:0.00} in from left, {a.Y:0.00} in from top";
            return $"{size}{position}\n{px.Width:N0} × {px.Height:N0} pixels at {SelectedDpi} dpi";
        }
    }

    public string? AdjustmentNote => CurrentCapabilities is { } c && (c.Brightness is null || c.Contrast is null)
        ? "This scanner has no hardware brightness/contrast control; adjustments are applied after scanning."
        : null;

    private SourceCapabilities? CurrentCapabilities => _capabilities?.For(SelectedSource);
    private SizeInches Bed => new(BedSize.Width, BedSize.Height);

    // ---- Startup / shutdown ---------------------------------------------------------------

    public Task InitializeAsync() => RefreshDevicesCommand.ExecuteAsync(null);

    public bool ConfirmClose()
    {
        if (IsScanning && !_dialogs.Confirm("A scan is in progress. Close anyway?", "Scan in progress"))
            return false;
        if (!HasUnsavedChanges || Pages.Count == 0)
            return true;
        return _dialogs.Confirm(
            $"You have {PageCountText.ToLowerInvariant()} that haven't been saved. Close ScanManager and discard them?",
            "Unsaved pages");
    }

    public void SaveSettings()
    {
        _settings.LastDeviceId = SelectedDevice?.Id ?? _settings.LastDeviceId;
        _settings.Source = SelectedSource;
        _settings.ColorMode = SelectedColorMode;
        _settings.Dpi = SelectedDpi;
        _settings.PaperSizeName = SelectedAreaPreset?.Name;
        _settings.CustomArea = SelectedAreaPreset?.IsCustom == true ? Area : null;
        _settings.Brightness = Brightness;
        _settings.Contrast = Contrast;
        _settings.BlackWhiteThreshold = BlackWhiteThreshold;
        _settings.SkipBlankPages = SkipBlankPages;
        _settingsStore.Save(_settings);
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _registry.Dispose();
        _store.Dispose();
    }

    // ---- Devices and capabilities ---------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshDevicesAsync()
    {
        string? previousId = SelectedDevice?.Id ?? _settings.LastDeviceId;
        IsBusy = true;
        StatusText = "Looking for scanners…";
        IReadOnlyList<ScannerInfo> devices;
        IReadOnlyList<string> errors;
        try
        {
            (devices, errors) = await _registry.GetDevicesAsync();
        }
        finally
        {
            IsBusy = false;
        }

        Devices.Clear();
        foreach (var device in devices) Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(d => string.Equals(d.Id, previousId, StringComparison.OrdinalIgnoreCase))
                         ?? Devices.FirstOrDefault();

        if (Devices.Count == 0)
            StatusText = errors.Count > 0 ? errors[0] : "No scanners found. Connect or turn on a scanner, then click Refresh.";
    }

    partial void OnSelectedDeviceChanged(ScannerInfo? value)
    {
        OnPropertyChanged(nameof(CanPreview));
        RefreshCommands();
        _ = LoadCapabilitiesAsync(value);
    }

    private async Task LoadCapabilitiesAsync(ScannerInfo? device)
    {
        int request = ++_capabilitiesRequest;
        _capabilities = null;
        _flatbedPreview = null;
        PreviewImage = null;

        if (device is null)
        {
            IsBusy = false;
            UpdateSources();
            return;
        }

        IsBusy = true;
        StatusText = $"Connecting to {device.Name}…";
        try
        {
            var capabilities = await _registry.BackendFor(device).GetCapabilitiesAsync(device.Id);
            if (request != _capabilitiesRequest) return;
            _capabilities = capabilities;
            StatusText = $"Ready: {device.Name}";
        }
        catch (ScannerException ex)
        {
            if (request == _capabilitiesRequest)
                StatusText = $"Couldn't read the scanner's capabilities. {ex.Message}";
        }
        finally
        {
            if (request == _capabilitiesRequest) IsBusy = false;
        }

        if (request == _capabilitiesRequest) UpdateSources();
    }

    private void UpdateSources()
    {
        var available = _capabilities?.Sources.Select(s => s.Source).ToList() ?? [ScanSource.Flatbed];
        ReplaceItems(Sources, available);

        var source = available.Contains(SelectedSource) ? SelectedSource : available[0];
        if (source != SelectedSource)
        {
            SelectedSource = source; // applies the source's capabilities
        }
        else
        {
            // Rebuilding the list cleared the combo box selection; re-announce it.
            OnPropertyChanged(nameof(SelectedSource));
            ApplySourceCapabilities();
        }
    }

    partial void OnSelectedSourceChanged(ScanSource value)
    {
        if (_initializing) return;
        ApplySourceCapabilities();
    }

    /// <summary>Updates resolutions, bed size and paper presets for the current device and source.</summary>
    private void ApplySourceCapabilities()
    {
        var caps = CurrentCapabilities;

        IReadOnlyList<int> resolutions = caps is { Resolutions.Count: > 0 } ? caps.Resolutions : DefaultResolutions;
        int wantedDpi = SelectedDpi > 0 ? SelectedDpi : _settings.Dpi;
        ReplaceItems(Resolutions, resolutions);
        SelectedDpi = resolutions.MinBy(r => Math.Abs(r - wantedDpi));
        OnPropertyChanged(nameof(SelectedDpi));

        var bed = caps?.MaxSize ?? DefaultBed;
        string presetName = SelectedAreaPreset?.Name ?? _settings.PaperSizeName ?? AreaPreset.FullAreaName;
        var previousArea = Area;

        _syncingArea = true;
        try
        {
            BedSize = new Size(bed.Width, bed.Height);
            var presets = new List<AreaPreset> { AreaPreset.FullArea };
            presets.AddRange(PaperSize.Standard.Where(p => p.Size.Fits(bed)).Select(p => new AreaPreset(p.Name, p.Size)));
            presets.Add(AreaPreset.Custom);
            ReplaceItems(AreaPresets, presets);
            SelectedAreaPreset = null;
            SelectedAreaPreset = presets.FirstOrDefault(p => p.Name == presetName) ?? AreaPreset.FullArea;
        }
        finally
        {
            _syncingArea = false;
        }

        var preset = SelectedAreaPreset!;
        SetArea(preset.IsCustom ? previousArea.ClampTo(bed) : preset.AreaFor(bed));

        PreviewImage = SelectedSource == ScanSource.Flatbed ? _flatbedPreview : null;
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(AdjustmentNote));
        OnPropertyChanged(nameof(AreaDescription));
        RefreshCommands();
    }

    // ---- Scan area ------------------------------------------------------------------------

    partial void OnSelectedAreaPresetChanged(AreaPreset? value)
    {
        if (_syncingArea || value is null || value.IsCustom) return;
        SetArea(value.AreaFor(Bed));
    }

    partial void OnNormalizedAreaChanged(Rect value)
    {
        if (_syncingArea) return;

        // The user moved the frame: update the area and show which preset (if any) it matches.
        var area = ScanArea.FromNormalized(value.X, value.Y, value.Width, value.Height, Bed).ClampTo(Bed);
        _syncingArea = true;
        try
        {
            Area = area;
            SelectedAreaPreset = AreaPresets.FirstOrDefault(p => !p.IsCustom && p.AreaFor(Bed).ApproximatelyEquals(area, 0.05))
                                 ?? AreaPreset.Custom;
        }
        finally
        {
            _syncingArea = false;
        }
    }

    private void SetArea(ScanArea area)
    {
        _syncingArea = true;
        try
        {
            Area = area;
            var n = area.ToNormalized(Bed);
            NormalizedArea = new Rect(n.X, n.Y, n.Width, n.Height);
        }
        finally
        {
            _syncingArea = false;
        }
    }

    [RelayCommand]
    private void ResetAdjustments()
    {
        Brightness = 0;
        Contrast = 0;
        BlackWhiteThreshold = DefaultThreshold;
    }

    // ---- Scanning -------------------------------------------------------------------------

    private bool CanRunPreview => IsIdle && CanPreview;
    private bool CanScan => IsIdle && SelectedDevice is not null;

    [RelayCommand(CanExecute = nameof(CanRunPreview))]
    private async Task PreviewAsync()
    {
        var settings = new ScanSettings
        {
            Source = ScanSource.Flatbed,
            ColorMode = SelectedColorMode,
            Dpi = CurrentCapabilities?.PreviewResolution ?? 100,
            IsPreview = true,
            Brightness = Brightness,
            Contrast = Contrast,
        };
        var options = ProcessingOptions(detectBlankPages: false);
        ViewIndex = 0;

        var outcome = await RunScanAsync(settings, "Scanning preview…", async image =>
        {
            var processed = await Task.Run(() => ImageTools.ProcessScan(image, options));
            _flatbedPreview = processed.Image;
            if (SelectedSource == ScanSource.Flatbed) PreviewImage = processed.Image;
        });

        if (outcome == ScanOutcome.Completed)
            StatusText = "Preview ready. Drag the frame to choose the area to scan, then click Scan.";
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var settings = new ScanSettings
        {
            Source = SelectedSource,
            ColorMode = SelectedColorMode,
            Dpi = SelectedDpi,
            Area = Area,
            Brightness = Brightness,
            Contrast = Contrast,
        };
        var options = ProcessingOptions(detectBlankPages: SkipBlankPages && IsFeederSource);
        var added = new List<PageViewModel>();
        int skipped = 0;
        bool feeder = IsFeederSource;

        var outcome = await RunScanAsync(settings, feeder ? "Scanning from the document feeder…" : "Scanning…", async image =>
        {
            StatusText = $"Processing page {image.PageNumber}…";
            var page = await Task.Run(() =>
            {
                var processed = ImageTools.ProcessScan(image, options);
                return processed.IsBlank ? null : PageViewModel.Create(_store.Save(processed.Image), processed.Image);
            });

            if (page is null)
            {
                skipped++;
            }
            else
            {
                Pages.Add(page);
                added.Add(page);
                SelectedPage = page;
                HasUnsavedChanges = true;
            }
            if (feeder) StatusText = $"{Summary(added.Count, skipped)}. Scanning the next page…";
        });

        if (added.Count > 0)
            PushUndo(added.Count == 1 ? "scan" : $"scan of {added.Count} pages", () => { foreach (var p in added) Pages.Remove(p); });

        if (outcome == ScanOutcome.Completed)
            StatusText = Summary(added.Count, skipped) + ".";
        else if (outcome == ScanOutcome.Cancelled)
            StatusText = $"Scan cancelled. {Summary(added.Count, skipped)}.";
    }

    private static string Summary(int scanned, int skipped)
    {
        string text = scanned == 1 ? "Scanned 1 page" : $"Scanned {scanned} pages";
        return skipped switch
        {
            0 => text,
            1 => $"{text}, skipped 1 blank page",
            _ => $"{text}, skipped {skipped} blank pages",
        };
    }

    private ScanProcessingOptions ProcessingOptions(bool detectBlankPages) => new(
        SelectedColorMode, Brightness, Contrast, (byte)Math.Clamp(BlackWhiteThreshold, 1, 255), detectBlankPages);

    private async Task<ScanOutcome> RunScanAsync(ScanSettings settings, string message, Func<ScannedImage, Task> onImage)
    {
        if (SelectedDevice is not { } device) return ScanOutcome.Failed;

        using var cts = new CancellationTokenSource();
        _scanCts = cts;
        IsBusy = true;
        IsScanning = true;
        StatusText = message;
        try
        {
            await foreach (var image in _registry.BackendFor(device).ScanAsync(device.Id, settings, cts.Token))
                await onImage(image);
            return ScanOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
            return ScanOutcome.Cancelled;
        }
        catch (ScannerException ex)
        {
            StatusText = ex.Message;
            _dialogs.ShowError(ex.Message, "Scan failed");
            return ScanOutcome.Failed;
        }
        finally
        {
            _scanCts = null;
            IsScanning = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        _scanCts?.Cancel();
        StatusText = "Cancelling after the current page…";
    }

    // ---- Page viewing and editing ---------------------------------------------------------

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        IsCropMode = false;
        RefreshCommands();
        _ = LoadSelectedPageImageAsync(value);
        if (value is not null) ViewIndex = 1;
    }

    partial void OnIsCropModeChanged(bool value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnIsScanningChanged(bool value) => RefreshCommands();

    partial void OnViewIndexChanged(int value)
    {
        if (value != 1) IsCropMode = false;
    }

    private async Task LoadSelectedPageImageAsync(PageViewModel? page)
    {
        if (page is null)
        {
            SelectedPageImage = null;
            _selectedPageImagePath = null;
            return;
        }

        string path = page.FilePath;
        try
        {
            var image = await Task.Run(() => ImageTools.LoadFile(path));
            if (SelectedPage == page && page.FilePath == path)
            {
                SelectedPageImage = image;
                _selectedPageImagePath = path;
            }
        }
        catch (IOException ex)
        {
            StatusText = $"Couldn't open the page image. {ex.Message}";
        }
    }

    private bool CanEditPage => IsIdle && SelectedPage is not null && !IsCropMode;
    private bool CanMovePageUp => CanEditPage && Pages.IndexOf(SelectedPage!) > 0;
    private bool CanMovePageDown => CanEditPage && Pages.IndexOf(SelectedPage!) < Pages.Count - 1;
    private bool CanUsePages => IsIdle && Pages.Count > 0;
    private bool CanFinishCrop => IsIdle && IsCropMode;

    [RelayCommand(CanExecute = nameof(CanEditPage))]
    private Task RotateLeftAsync() => TransformPageAsync("rotate", image => ImageTools.Rotate(image, 270));

    [RelayCommand(CanExecute = nameof(CanEditPage))]
    private Task RotateRightAsync() => TransformPageAsync("rotate", image => ImageTools.Rotate(image, 90));

    [RelayCommand(CanExecute = nameof(CanEditPage))]
    private Task Rotate180Async() => TransformPageAsync("rotate", image => ImageTools.Rotate(image, 180));

    [RelayCommand(CanExecute = nameof(CanEditPage))]
    private void StartCrop()
    {
        ViewIndex = 1;
        CropSelection = new Rect(0.03, 0.03, 0.94, 0.94);
        IsCropMode = true;
        StatusText = "Drag the frame or its handles to choose what to keep, then click Apply crop.";
    }

    [RelayCommand(CanExecute = nameof(CanFinishCrop))]
    private async Task ApplyCropAsync()
    {
        if (SelectedPage is not { } page) return;
        var rect = ImageTools.ToPixelRect(CropSelection, page.PixelWidth, page.PixelHeight);
        IsCropMode = false;
        if (rect.Width == page.PixelWidth && rect.Height == page.PixelHeight) return;
        await TransformPageAsync("crop", image => ImageTools.Crop(image, rect));
        StatusText = $"Cropped page {page.Number} to {rect.Width} × {rect.Height} pixels.";
    }

    [RelayCommand(CanExecute = nameof(CanFinishCrop))]
    private void CancelCrop()
    {
        IsCropMode = false;
        StatusText = "Crop cancelled.";
    }

    private async Task TransformPageAsync(string name, Func<BitmapSource, BitmapSource> transform)
    {
        if (SelectedPage is not { } page) return;
        var before = page.State;
        // Reuse the image already on screen when it is current, to skip decoding the file again.
        var current = _selectedPageImagePath == before.FilePath ? SelectedPageImage : null;

        IsBusy = true;
        try
        {
            var (state, image) = await Task.Run(() =>
            {
                var source = current ?? ImageTools.LoadFile(before.FilePath);
                var result = transform(source);
                return (PageState.From(_store.Save(result), result), result);
            });

            page.Apply(state);
            if (SelectedPage == page)
            {
                SelectedPageImage = image;
                _selectedPageImagePath = state.FilePath;
            }
            HasUnsavedChanges = true;
            PushUndo(name, () => RestorePage(page, before));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _dialogs.ShowError($"Couldn't {name} the page. {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RestorePage(PageViewModel page, PageState state)
    {
        page.Apply(state);
        if (SelectedPage == page)
            _ = LoadSelectedPageImageAsync(page);
        else
            SelectedPage = page;
    }

    [RelayCommand(CanExecute = nameof(CanEditPage))]
    private void DeletePage()
    {
        if (SelectedPage is not { } page) return;
        int index = Pages.IndexOf(page);
        Pages.RemoveAt(index);
        HasUnsavedChanges = true;
        SelectedPage = Pages.Count == 0 ? null : Pages[Math.Min(index, Pages.Count - 1)];
        if (Pages.Count == 0) ViewIndex = 0;
        StatusText = $"Deleted page {index + 1}.";

        PushUndo("delete", () =>
        {
            Pages.Insert(Math.Min(index, Pages.Count), page);
            SelectedPage = page;
        });
    }

    [RelayCommand(CanExecute = nameof(CanMovePageUp))]
    private void MovePageUp()
    {
        if (SelectedPage is { } page) MovePage(page, Pages.IndexOf(page) - 1);
    }

    [RelayCommand(CanExecute = nameof(CanMovePageDown))]
    private void MovePageDown()
    {
        if (SelectedPage is { } page) MovePage(page, Pages.IndexOf(page) + 1);
    }

    /// <summary>Moves a page to a new position (also used by drag and drop in the page list).</summary>
    public void MovePage(PageViewModel page, int newIndex)
    {
        if (IsBusy) return;
        int oldIndex = Pages.IndexOf(page);
        if (oldIndex < 0) return;
        newIndex = Math.Clamp(newIndex, 0, Pages.Count - 1);
        if (oldIndex == newIndex) return;

        Pages.Move(oldIndex, newIndex);
        SelectedPage = page;
        HasUnsavedChanges = true;
        RefreshCommands();

        PushUndo("move", () =>
        {
            int current = Pages.IndexOf(page);
            if (current >= 0) Pages.Move(current, Math.Min(oldIndex, Pages.Count - 1));
            SelectedPage = page;
        });
    }

    [RelayCommand(CanExecute = nameof(CanUsePages))]
    private void ClearPages()
    {
        if (!_dialogs.Confirm($"Remove all {Pages.Count} pages from the document? You can bring them back with Undo.", "Clear pages"))
            return;

        var removed = Pages.ToList();
        SelectedPage = null;
        Pages.Clear();
        ViewIndex = 0;
        HasUnsavedChanges = false;
        StatusText = "Cleared all pages.";

        PushUndo("clear", () =>
        {
            foreach (var p in removed) Pages.Add(p);
            SelectedPage = removed.FirstOrDefault();
        });
    }

    // ---- Undo -----------------------------------------------------------------------------

    private bool CanUndo => IsIdle && _undo.Count > 0;

    private void PushUndo(string name, Action undo)
    {
        _undo.Add(new UndoStep(name, undo));
        if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
        OnPropertyChanged(nameof(UndoDescription));
        UndoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        IsCropMode = false;
        step.Undo();
        HasUnsavedChanges = Pages.Count > 0;
        StatusText = $"Undid {step.Name}.";
        OnPropertyChanged(nameof(UndoDescription));
        RefreshCommands();
    }

    // ---- Files ----------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanUsePages))]
    private async Task SaveAsync()
    {
        var defaultFormat = Enum.IsDefined(typeof(ExportFormat), _settings.LastSaveFormatIndex)
            ? (ExportFormat)_settings.LastSaveFormatIndex
            : ExportFormat.Pdf;
        string folder = _settings.LastSaveFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var target = _dialogs.AskSaveTarget($"Scan {DateTime.Now:yyyy-MM-dd HHmm}", folder, defaultFormat);
        if (target is null) return;

        var files = Pages.Select(p => p.FilePath).ToList();
        int quality = _settings.JpegQuality;
        IsBusy = true;
        StatusText = $"Saving {PageCountText.ToLowerInvariant()}…";
        try
        {
            var written = await Task.Run(() => DocumentExporter.Save(files, target.Path, target.Format, quality));
            _settings.LastSaveFolder = Path.GetDirectoryName(target.Path);
            _settings.LastSaveFormatIndex = (int)target.Format;
            HasUnsavedChanges = false;
            StatusText = written.Count == 1
                ? $"Saved {target.Path}"
                : $"Saved {written.Count} files to {Path.GetDirectoryName(target.Path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            StatusText = "Save failed.";
            _dialogs.ShowError($"Couldn't save the file.\n\n{ex.Message}", "Save failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsePages))]
    private void Print()
    {
        try
        {
            var files = Pages.Select(p => p.FilePath).ToList();
            if (_dialogs.Print(files, "ScanManager"))
                StatusText = $"Sent {PageCountText.ToLowerInvariant()} to the printer.";
        }
        catch (Exception ex)
        {
            // Printing goes through the spooler and printer drivers, which can fail in many ways.
            _dialogs.ShowError($"Printing failed.\n\n{ex.Message}", "Print");
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportAsync()
    {
        var files = _dialogs.AskImportFiles(_settings.LastSaveFolder);
        if (files is { Count: > 0 })
            await ImportFilesAsync(files);
    }

    /// <summary>Adds image files (every frame of multi-page TIFFs) as pages. Also used for drag and drop.</summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> files)
    {
        if (IsBusy || files.Count == 0) return;

        var added = new List<PageViewModel>();
        var failed = new List<string>();
        IsBusy = true;
        try
        {
            foreach (var file in files)
            {
                StatusText = $"Adding {Path.GetFileName(file)}…";
                try
                {
                    var pages = await Task.Run(() => ImageTools.LoadAllFrames(file)
                        .Select(frame =>
                        {
                            var image = ImageTools.NormalizeImported(frame);
                            return PageViewModel.Create(_store.Save(image), image);
                        })
                        .ToList());
                    foreach (var page in pages)
                    {
                        Pages.Add(page);
                        added.Add(page);
                    }
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    failed.Add(Path.GetFileName(file));
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (added.Count > 0)
        {
            HasUnsavedChanges = true;
            SelectedPage = added[0];
            PushUndo("add images", () => { foreach (var p in added) Pages.Remove(p); });
        }
        StatusText = added.Count == 1 ? "Added 1 page." : $"Added {added.Count} pages.";
        if (failed.Count > 0)
            _dialogs.ShowError($"These files couldn't be opened as images:\n\n{string.Join("\n", failed)}", "Add images");
    }

    // ---- Helpers --------------------------------------------------------------------------

    private void RefreshCommands()
    {
        IRelayCommand[] commands =
        [
            RefreshDevicesCommand, PreviewCommand, ScanCommand, CancelScanCommand, SaveCommand, PrintCommand,
            ImportCommand, UndoCommand, ClearPagesCommand, RotateLeftCommand, RotateRightCommand, Rotate180Command,
            StartCropCommand, ApplyCropCommand, CancelCropCommand, DeletePageCommand, MovePageUpCommand, MovePageDownCommand,
        ];
        foreach (var command in commands)
            command.NotifyCanExecuteChanged();
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var item in list) target.Add(item);
    }
}
