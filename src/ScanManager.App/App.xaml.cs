using System.Windows;
using System.Windows.Threading;
using ScanManager.App.Services;
using ScanManager.App.ViewModels;
using ScanManager.Core.Scanning;
using ScanManager.Core.Settings;
using ScanManager.Wia;

namespace ScanManager.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var backends = new List<IScannerBackend> { new WiaScannerBackend() };
        bool simulator = e.Args.Contains("--simulator", StringComparer.OrdinalIgnoreCase);
#if DEBUG
        simulator = true;
#endif
        if (simulator) backends.Add(new SimulatedScannerBackend());

        var window = new MainWindow();
        _viewModel = new MainViewModel(new ScannerRegistry(backends), new PageStore(), new DialogService(window), SettingsStore.CreateDefault());
        window.DataContext = _viewModel;
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, $"Something went wrong:\n\n{e.Exception.Message}", "ScanManager",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
