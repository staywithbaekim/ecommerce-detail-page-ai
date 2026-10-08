using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Desktop.App;

/// <summary>A bounded bitmap of the complete document, with structural coordinates, never a synthetic layout.</summary>
public sealed class TemplateRulePreview : FrameworkElement
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(nameof(Page), typeof(TemplateRulePageViewModel),
        typeof(TemplateRulePreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, PageChanged));
    public static readonly DependencyProperty FitWholeProperty = DependencyProperty.Register(nameof(FitWhole), typeof(bool),
        typeof(TemplateRulePreview), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    private ImageSource? _image;
    private double _scale = 1;
    public TemplateRulePageViewModel? Page { get => (TemplateRulePageViewModel?)GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public bool FitWhole { get => (bool)GetValue(FitWholeProperty); set => SetValue(FitWholeProperty, value); }
    public bool HasComposite => _image is not null;
    public double DocumentScale => _scale;
    public event Action<TemplateRuleFrameViewModel>? FrameInvoked;

    public TemplateRulePreview()
    {
        Loaded += (_, _) =>
        {
            Subscribe(); LoadComposite();
            _ = Dispatcher.BeginInvoke(() =>
            { if (Page?.SelectedFrame is { } selected && IsLoaded) BringIntoView(FrameRect(selected)); });
        };
        Unloaded += (_, _) => { if (Page is { } page) page.PropertyChanged -= SelectionChanged; _image = null; };
        Focusable = true;
        ClipToBounds = true;
    }
    private static void PageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var preview = (TemplateRulePreview)sender;
        if (args.OldValue is TemplateRulePageViewModel old) old.PropertyChanged -= preview.SelectionChanged;
        preview.Subscribe();
        if (preview.IsLoaded) preview.LoadComposite();
    }
    private void Subscribe()
    {
        if (Page is not { } page) return;
        page.PropertyChanged -= SelectionChanged;
        page.PropertyChanged += SelectionChanged;
    }
    private void LoadComposite()
    {
        _image = new BoundedPreviewImageConverter().Convert(Page?.PreviewSource, typeof(ImageSource), "2048", CultureInfo.InvariantCulture) as ImageSource;
        InvalidateMeasure(); InvalidateVisual();
    }
    private void SelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TemplateRulePageViewModel.PreviewSource)) { LoadComposite(); return; }
        InvalidateVisual();
        if (args.PropertyName == nameof(TemplateRulePageViewModel.SelectedFrame) && Page?.SelectedFrame is { } frame)
            BringIntoView(FrameRect(frame));
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 360;
        if (Page is not { CanvasWidth: > 0, CanvasHeight: > 0 } page || _image is null) return new(width, 220);
        _scale = width / page.CanvasWidth;
        if (FitWhole) _scale = Math.Min(_scale, 480d / page.CanvasHeight);
        return new(width, page.CanvasHeight * _scale);
    }
    internal Rect FrameRect(TemplateRuleFrameViewModel frame) => frame.Target.PreviewBounds is { } bounds
        ? new(bounds.Left * _scale, bounds.Top * _scale, bounds.Width * _scale, bounds.Height * _scale)
        : new(frame.Target.Left * _scale, frame.Target.Top * _scale, frame.Target.Width * _scale, frame.Target.Height * _scale);
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Page is not { } page || _image is null)
        {
            var text = new FormattedText("PSD 合成预览不可用\n仍可编辑已登记的规则；不会生成示意图片。", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, ActualWidth - 24) };
            drawingContext.DrawText(text, new Point(12, 22)); return;
        }
        drawingContext.DrawImage(_image, new Rect(0, 0, page.CanvasWidth * _scale, page.CanvasHeight * _scale));
        var accent = TryFindResource("WorkbenchAccentBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(53, 100, 84));
        var quiet = new SolidColorBrush(Color.FromRgb(105, 116, 110));
        foreach (var frame in page.EditableFrames)
        {
            var selected = frame == page.SelectedFrame;
            var pen = new Pen(selected ? accent : quiet, selected ? 2.5 : 1);
            if (frame.IsCandidate) pen.DashStyle = DashStyles.Dash;
            drawingContext.DrawRectangle(selected ? new SolidColorBrush(Color.FromArgb(26, 53, 100, 84)) : null,
                pen, FrameRect(frame));
            var rect = FrameRect(frame);
            var label = new FormattedText(frame.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var location = new Point(Math.Clamp(rect.Left, 0, Math.Max(0, ActualWidth - label.Width - 10)), Math.Max(0, rect.Top));
            drawingContext.DrawRoundedRectangle(selected ? accent : quiet, null,
                new Rect(location, new Size(label.Width + 10, label.Height + 6)), 3, 3);
            drawingContext.DrawText(label, new Point(location.X + 5, location.Y + 3));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_image is null || Page is not { } page) return;
        var point = e.GetPosition(this);
        var frame = page.EditableFrames.Where(frame => FrameRect(frame).Contains(point))
            .OrderBy(frame => FrameRect(frame).Width * FrameRect(frame).Height).FirstOrDefault();
        if (frame is null) return;
        page.SelectedFrame = frame; FrameInvoked?.Invoke(frame); e.Handled = true;
    }
}
