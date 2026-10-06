using System.Windows;
using ScanManager.App.Services;
using ScanManager.Core.Scanning;

namespace ScanManager.App;

public partial class SettingsWindow : Window
{
    private sealed record SourceOption(string Name, ScanSource? Value);

    private static readonly SourceOption[] SourceOptions =
    [
        new("The one I used last", null),
        new(DisplayNames.For(ScanSource.Flatbed), ScanSource.Flatbed),
        new(DisplayNames.For(ScanSource.Feeder), ScanSource.Feeder),
        new(DisplayNames.For(ScanSource.Duplex), ScanSource.Duplex),
    ];

    public SettingsWindow(Preferences current)
    {
        InitializeComponent();
        DefaultSourceBox.ItemsSource = SourceOptions;
        DefaultSourceBox.SelectedItem = SourceOptions.First(o => o.Value == current.DefaultSource);
    }

    /// <summary>The edited preferences, set when the user clicks Save.</summary>
    public Preferences? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new Preferences(((SourceOption)DefaultSourceBox.SelectedItem).Value);
        DialogResult = true;
    }
}
