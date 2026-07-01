using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LuckyLilliaDesktop.Utils;

namespace LuckyLilliaDesktop.Controls;

public partial class LoadingSpinner : UserControl
{
    public static readonly StyledProperty<double> SpinnerSizeProperty =
        AvaloniaProperty.Register<LoadingSpinner, double>(nameof(SpinnerSize), 32);

    private static readonly StyledProperty<IBrush?> SpinnerBrushProperty =
        AvaloniaProperty.Register<LoadingSpinner, IBrush?>(nameof(SpinnerBrush));

    public double SpinnerSize
    {
        get => GetValue(SpinnerSizeProperty);
        set => SetValue(SpinnerSizeProperty, value);
    }

    public IBrush? SpinnerBrush
    {
        get => GetValue(SpinnerBrushProperty);
        set => SetValue(SpinnerBrushProperty, value);
    }

    public LoadingSpinner()
    {
        InitializeComponent();
        if (RenderingPerformanceHelper.UseReducedMotion)
            Styles.Clear();
        UpdateSize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpinnerSizeProperty)
            UpdateSize();
        else if (change.Property == SpinnerBrushProperty)
            UpdateBrush();
    }

    private void UpdateSize()
    {
        Width = SpinnerSize;
        Height = SpinnerSize;
        SpinnerArc.Width = SpinnerSize;
        SpinnerArc.Height = SpinnerSize;
    }

    private void UpdateBrush()
    {
        if (SpinnerBrush != null)
            SpinnerArc.Stroke = SpinnerBrush;
    }
}
