using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScanManager.App.ViewModels;

namespace ScanManager.App;

public partial class MainWindow : Window
{
    private Point _dragStart;
    private PageViewModel? _dragPage;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
        Drop += Window_Drop;
        DragOver += Window_DragOver;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!ViewModel.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        ViewModel.SaveSettings();
    }

    // ---- Reordering pages by dragging them in the list ------------------------------------

    private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragPage = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PageViewModel;
    }

    private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragPage is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var page = _dragPage;
        _dragPage = null;
        DragDrop.DoDragDrop(PageList, new DataObject(typeof(PageViewModel), page), DragDropEffects.Move);
    }

    private void PageList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PageViewModel)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void PageList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PageViewModel)) is not PageViewModel page) return;

        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PageViewModel;
        int index = target is null ? ViewModel.Pages.Count - 1 : ViewModel.Pages.IndexOf(target);
        ViewModel.MovePage(page, index);
        e.Handled = true;
    }

    // ---- Dropping image files from Explorer -----------------------------------------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        await ViewModel.ImportFilesAsync(files);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as T;
    }
}
