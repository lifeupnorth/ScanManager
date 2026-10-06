using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ScanManager.App.Imaging;
using ScanManager.Core.Scanning;

namespace ScanManager.App.Services;

public sealed record SaveTarget(string Path, ExportFormat Format);

/// <summary>The options edited in the Settings window.</summary>
public sealed record Preferences(ScanSource? DefaultSource);

/// <summary>Everything the view model needs from the user that involves a window.</summary>
public interface IDialogService
{
    SaveTarget? AskSaveTarget(string defaultFileName, string? initialFolder, ExportFormat defaultFormat);
    IReadOnlyList<string>? AskImportFiles(string? initialFolder);
    bool Confirm(string message, string title);
    void ShowError(string message, string title = "ScanManager");

    /// <summary>Shows the Settings window. Returns null if the user cancelled.</summary>
    Preferences? EditPreferences(Preferences current);

    /// <summary>Shows the print dialog and prints the pages. Returns false if the user cancelled.</summary>
    bool Print(IReadOnlyList<string> pageFiles, string jobName);
}

public sealed class DialogService(Window owner) : IDialogService
{
    public SaveTarget? AskSaveTarget(string defaultFileName, string? initialFolder, ExportFormat defaultFormat)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save scanned pages",
            Filter = DocumentExporter.DialogFilter,
            FilterIndex = (int)defaultFormat,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (initialFolder is not null && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;

        if (dialog.ShowDialog(owner) != true) return null;

        // A typed extension ("receipt.tif") wins over the file-type dropdown.
        string path = dialog.FileName;
        if (DocumentExporter.FormatFromExtension(Path.GetExtension(path)) is { } typed)
            return new SaveTarget(path, typed);

        var format = Enum.IsDefined(typeof(ExportFormat), dialog.FilterIndex) ? (ExportFormat)dialog.FilterIndex : ExportFormat.Pdf;
        return new SaveTarget(path + DocumentExporter.Extension(format), format);
    }

    public IReadOnlyList<string>? AskImportFiles(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add images as pages",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.gif|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (initialFolder is not null && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : null;
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string message, string title = "ScanManager") =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public Preferences? EditPreferences(Preferences current)
    {
        var window = new SettingsWindow(current) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public bool Print(IReadOnlyList<string> pageFiles, string jobName)
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = false };
        if (dialog.ShowDialog() != true) return false;

        var pageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        const double margin = 24; // keep clear of the printer's unprintable edge (1/4 inch)
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = pageSize;

        foreach (var file in pageFiles)
        {
            BitmapSource image = ImageTools.LoadFile(file);
            double width = image.PixelWidth / ValidDpi(image.DpiX) * 96;
            double height = image.PixelHeight / ValidDpi(image.DpiY) * 96;

            // Turn landscape scans sideways on portrait paper (and vice versa) to use the sheet best.
            if (width > height != pageSize.Width > pageSize.Height)
            {
                image = ImageTools.Rotate(image, 90);
                (width, height) = (height, width);
            }

            // Print at true size, shrinking only when the scan is larger than the paper.
            double scale = Math.Min(1.0, Math.Min((pageSize.Width - 2 * margin) / width, (pageSize.Height - 2 * margin) / height));
            var picture = new Image { Source = image, Width = width * scale, Height = height * scale, Stretch = System.Windows.Media.Stretch.Fill };
            var page = new FixedPage { Width = pageSize.Width, Height = pageSize.Height };
            FixedPage.SetLeft(picture, (pageSize.Width - picture.Width) / 2);
            FixedPage.SetTop(picture, margin);
            page.Children.Add(picture);

            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            document.Pages.Add(content);
        }

        dialog.PrintDocument(document.DocumentPaginator, jobName);
        return true;
    }

    private static double ValidDpi(double dpi) => dpi is > 1 and < 10000 ? dpi : 96;
}
