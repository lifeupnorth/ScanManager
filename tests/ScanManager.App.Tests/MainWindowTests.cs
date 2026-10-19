using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ScanManager.App.Controls;
using ScanManager.App.Imaging;
using ScanManager.App.Services;
using ScanManager.App.ViewModels;
using ScanManager.Core.Scanning;
using ScanManager.Core.Settings;
using Edge = ScanManager.App.Controls.RegionSelector.Edge;

namespace ScanManager.App.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScanManagerAppTests", Guid.NewGuid().ToString("N"));

    public MainWindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Screen_readers_get_a_name_for_every_setting_and_page()
    {
        RunOnSta(() =>
        {
            // Loads App.xaml's resources (styles, converters, theme) without starting the app.
            new App().InitializeComponent();
            using var vm = new MainViewModel(new ScannerRegistry([]), new PageStore(), new NoDialogs(),
                new SettingsStore(Path.Combine(_dir, "settings.json")));
            string png = Path.Combine(_dir, "page.png");
            var image = ImageTools.Create(10, 10, 96, PixelFormats.Gray8, new byte[100], 10);
            ImageTools.SavePng(image, png);
            vm.Pages.Add(PageViewModel.Create(png, image));

            var window = new MainWindow { DataContext = vm, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();
            try
            {
                var inputs = Descendants(window).OfType<Control>().Where(e => e is ComboBox or Slider).ToList();
                Assert.Equal(8, inputs.Count); // scanner, source, color, resolution, area; three sliders
                Assert.All(inputs, e => Assert.False(string.IsNullOrEmpty(Peer(e).GetName()), $"{e.GetType().Name} has no name"));

                var color = inputs.OfType<ComboBox>().Single(c => AutomationPropertiesName(c) == "Color");
                color.IsDropDownOpen = true; // its items exist once the list has been opened, as when Narrator reads them
                color.UpdateLayout();
                Assert.Equal(["Color", "Grayscale", "Black & white"], Peer(color).GetChildren().Select(p => p.GetName()));

                var pages = (ListBox)window.FindName("PageList");
                Assert.Equal("Page 1", Peer(pages).GetChildren().Single().GetName());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_press_inside_a_frame_that_fills_the_content_draws_a_new_frame()
    {
        var content = new Rect(0, 0, 400, 500);
        var center = new Point(200, 250);

        Assert.Equal(Edge.None, RegionSelector.HitTest(center, content, content));
        Assert.Equal(Edge.Move, RegionSelector.HitTest(center, new Rect(50, 50, 300, 400), content));
        Assert.Equal(Edge.Right | Edge.Bottom, RegionSelector.HitTest(new Point(399, 499), content, content));
    }

    [Fact]
    public void A_failed_tiff_export_leaves_no_page_file_open()
    {
        string page = Path.Combine(_dir, "page.png");
        ImageTools.SavePng(ImageTools.Create(10, 10, 96, PixelFormats.Gray8, new byte[100], 10), page);
        string missing = Path.Combine(_dir, "gone.png");

        Assert.Throws<FileNotFoundException>(() =>
            DocumentExporter.Save([page, missing], Path.Combine(_dir, "out.tif"), ExportFormat.Tiff, 85));

        File.Delete(page); // fails while a handle from the export is still open
        Assert.False(File.Exists(page));
    }

    private static AutomationPeer Peer(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element);

    private static string AutomationPropertiesName(DependencyObject element) =>
        System.Windows.Automation.AutomationProperties.GetName(element);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandchild in Descendants(child))
                yield return grandchild;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new AggregateException(error);
    }

    private sealed class NoDialogs : IDialogService
    {
        public Preferences? EditPreferences(Preferences current) => null;
        public SaveTarget? AskSaveTarget(string defaultFileName, string? initialFolder, ExportFormat defaultFormat) => null;
        public IReadOnlyList<string>? AskImportFiles(string? initialFolder) => null;
        public bool Confirm(string message, string title) => true;
        public void ShowError(string message, string title = "ScanManager") => throw new InvalidOperationException(message);
        public bool Print(IReadOnlyList<string> pageFiles, string jobName) => false;
    }
}
