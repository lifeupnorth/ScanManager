using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScanManager.App.Controls;

/// <summary>
/// Shows an image (or an empty sheet) fitted to the available space, with an optional
/// resizable, movable selection frame. The selection is expressed in fractions (0..1)
/// of the content, so it is independent of zoom and of the image's resolution. Used to
/// choose the scan area on the preview and to crop scanned pages.
/// </summary>
public sealed class RegionSelector : FrameworkElement
{
    private const double Padding = 16;
    private const double HandleSize = 9;
    private const double GripTolerance = 6;
    private const double MinSelectionPixels = 8;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(RegionSelector),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Size of the content in any unit (inches, pixels); only the aspect ratio matters.</summary>
    public static readonly DependencyProperty ContentSizeProperty = DependencyProperty.Register(
        nameof(ContentSize), typeof(Size), typeof(RegionSelector),
        new FrameworkPropertyMetadata(Size.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(Rect), typeof(RegionSelector),
        new FrameworkPropertyMetadata(new Rect(0, 0, 1, 1),
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ShowSelectionProperty = DependencyProperty.Register(
        nameof(ShowSelection), typeof(bool), typeof(RegionSelector),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(RegionSelector),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Size ContentSize { get => (Size)GetValue(ContentSizeProperty); set => SetValue(ContentSizeProperty, value); }
    public Rect Selection { get => (Rect)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public bool ShowSelection { get => (bool)GetValue(ShowSelectionProperty); set => SetValue(ShowSelectionProperty, value); }
    public string? PlaceholderText { get => (string?)GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }

    [Flags]
    internal enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private Edge _dragEdges;
    private bool _creating;
    private Point _dragStart;
    private Rect _startSelection;   // screen coordinates at drag start
    private Rect _selectionBeforeDrag; // normalised, restored if a new frame is too small

    public RegionSelector()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    private bool CanEdit => ShowSelection && IsEnabled;

    // ---- Geometry -----------------------------------------------------------------------

    private Size AspectSize
    {
        get
        {
            if (!ContentSize.IsEmpty && ContentSize.Width > 0 && ContentSize.Height > 0) return ContentSize;
            if (Source is { Width: > 0, Height: > 0 } s) return new Size(s.Width, s.Height);
            return new Size(8.5, 11);
        }
    }

    /// <summary>Where the content is drawn, in this control's coordinates.</summary>
    private Rect ContentRect
    {
        get
        {
            double availableWidth = ActualWidth - 2 * Padding, availableHeight = ActualHeight - 2 * Padding;
            if (availableWidth <= 1 || availableHeight <= 1) return Rect.Empty;
            var aspect = AspectSize;
            double scale = Math.Min(availableWidth / aspect.Width, availableHeight / aspect.Height);
            double w = aspect.Width * scale, h = aspect.Height * scale;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }
    }

    private Rect ToScreen(Rect normalized, Rect content) => new(
        content.X + normalized.X * content.Width,
        content.Y + normalized.Y * content.Height,
        normalized.Width * content.Width,
        normalized.Height * content.Height);

    private static Rect ToNormalized(Rect screen, Rect content) => new(
        (screen.X - content.X) / content.Width,
        (screen.Y - content.Y) / content.Height,
        screen.Width / content.Width,
        screen.Height / content.Height);

    private static Point Clamp(Point p, Rect r) =>
        new(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));

    // ---- Rendering ----------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent background so the whole control receives mouse input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var content = ContentRect;
        if (content.IsEmpty) return;

        var paperBorder = new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1);
        dc.DrawRectangle(Brushes.White, paperBorder, content);

        if (Source is not null)
        {
            dc.DrawImage(Source, content);
        }
        else if (!string.IsNullOrEmpty(PlaceholderText))
        {
            var text = new FormattedText(PlaceholderText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(10, content.Width - 40),
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(text, new Point(content.X + 20, content.Y + (content.Height - text.Height) / 2));
        }

        if (!ShowSelection) return;

        var selection = ToScreen(Selection, content);
        selection.Intersect(content);
        if (selection.IsEmpty) return;

        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(content), new RectangleGeometry(selection));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 20, 20, 20)), null, shade);

        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0, 120, 212));
        dc.DrawRectangle(null, new Pen(Brushes.White, 3), selection);
        dc.DrawRectangle(null, new Pen(accent, 1.5), selection);

        if (!CanEdit) return;
        var handlePen = new Pen(accent, 1.5);
        foreach (var point in HandlePoints(selection))
            dc.DrawRectangle(Brushes.White, handlePen, new Rect(point.X - HandleSize / 2, point.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    private static IEnumerable<Point> HandlePoints(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return r.TopLeft;
        yield return new Point(cx, r.Top);
        yield return r.TopRight;
        yield return new Point(r.Right, cy);
        yield return r.BottomRight;
        yield return new Point(cx, r.Bottom);
        yield return r.BottomLeft;
        yield return new Point(r.Left, cy);
    }

    // ---- Mouse --------------------------------------------------------------------------

    /// <summary>What a press at <paramref name="p"/> grabs: an edge or corner, the whole frame, or nothing (draw a new frame).</summary>
    internal static Edge HitTest(Point p, Rect selection, Rect content)
    {
        double t = GripTolerance + HandleSize / 2;
        bool nearLeft = Math.Abs(p.X - selection.Left) <= t, nearRight = Math.Abs(p.X - selection.Right) <= t;
        bool nearTop = Math.Abs(p.Y - selection.Top) <= t, nearBottom = Math.Abs(p.Y - selection.Bottom) <= t;
        bool withinX = p.X >= selection.Left - t && p.X <= selection.Right + t;
        bool withinY = p.Y >= selection.Top - t && p.Y <= selection.Bottom + t;

        var edge = Edge.None;
        if (withinY && nearLeft) edge |= Edge.Left;
        else if (withinY && nearRight) edge |= Edge.Right;
        if (withinX && nearTop) edge |= Edge.Top;
        else if (withinX && nearBottom) edge |= Edge.Bottom;
        // A frame covering all the content can't move, and leaves no room outside it to start a
        // new one, so inside it a drag draws a new frame instead.
        bool fillsContent = selection.Width >= content.Width - 1 && selection.Height >= content.Height - 1;
        if (edge == Edge.None && selection.Contains(p) && !fillsContent) edge = Edge.Move;
        return edge;
    }

    private static Cursor CursorFor(Edge edge) => edge switch
    {
        Edge.Move => Cursors.SizeAll,
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CanEdit) return;

        var content = ContentRect;
        if (content.IsEmpty) return;
        var p = e.GetPosition(this);
        var selection = ToScreen(Selection, content);
        var edge = HitTest(p, selection, content);

        if (edge == Edge.None)
        {
            if (!content.Contains(p)) return;
            // Start drawing a new frame from this corner.
            _creating = true;
            p = Clamp(p, content);
            selection = new Rect(p, p);
            edge = Edge.Right | Edge.Bottom;
        }

        Focus();
        _selectionBeforeDrag = Selection;
        _dragEdges = edge;
        _dragStart = p;
        _startSelection = selection;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var content = ContentRect;
        if (content.IsEmpty) return;
        var p = e.GetPosition(this);

        if (!IsMouseCaptured || _dragEdges == Edge.None)
        {
            Cursor = CanEdit ? CursorFor(HitTest(p, ToScreen(Selection, content), content)) : null;
            return;
        }

        Rect updated = _creating ? new Rect(_dragStart, Clamp(p, content)) : Drag(p, content);
        SetCurrentValue(SelectionProperty, ToNormalized(updated, content));
    }

    private Rect Drag(Point p, Rect content)
    {
        var s = _startSelection;
        if (_dragEdges == Edge.Move)
        {
            double dx = Math.Clamp(p.X - _dragStart.X, content.Left - s.Left, content.Right - s.Right);
            double dy = Math.Clamp(p.Y - _dragStart.Y, content.Top - s.Top, content.Bottom - s.Bottom);
            return new Rect(s.X + dx, s.Y + dy, s.Width, s.Height);
        }

        p = Clamp(p, content);
        double left = s.Left, top = s.Top, right = s.Right, bottom = s.Bottom;
        if (_dragEdges.HasFlag(Edge.Left)) left = Math.Min(p.X, right - MinSelectionPixels);
        if (_dragEdges.HasFlag(Edge.Right)) right = Math.Max(p.X, left + MinSelectionPixels);
        if (_dragEdges.HasFlag(Edge.Top)) top = Math.Min(p.Y, bottom - MinSelectionPixels);
        if (_dragEdges.HasFlag(Edge.Bottom)) bottom = Math.Max(p.Y, top + MinSelectionPixels);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;

        // A click without a real drag shouldn't collapse the selection to nothing.
        var content = ContentRect;
        if (_creating && !content.IsEmpty)
        {
            var s = ToScreen(Selection, content);
            if (s.Width < MinSelectionPixels || s.Height < MinSelectionPixels)
                SetCurrentValue(SelectionProperty, _selectionBeforeDrag);
        }
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragEdges = Edge.None;
        _creating = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!CanEdit) return;

        // Arrow keys nudge the frame by about a pixel on screen; Shift moves it ten times as far.
        var content = ContentRect;
        if (content.IsEmpty) return;
        double step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
        double dx = e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 } / content.Width;
        double dy = e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 } / content.Height;
        if (dx == 0 && dy == 0) return;

        var s = Selection;
        s.X = Math.Clamp(s.X + dx, 0, 1 - s.Width);
        s.Y = Math.Clamp(s.Y + dy, 0, 1 - s.Height);
        SetCurrentValue(SelectionProperty, s);
        e.Handled = true;
    }
}
