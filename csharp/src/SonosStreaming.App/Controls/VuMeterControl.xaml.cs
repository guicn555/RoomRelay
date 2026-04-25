using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace SonosStreaming.App.Controls;

public sealed class VuMeterControl : UserControl
{
    private readonly Rectangle _barL;
    private readonly Rectangle _barR;
    private readonly DispatcherTimer _timer;

    public static readonly DependencyProperty ValueLProperty =
        DependencyProperty.Register(nameof(ValueL), typeof(float), typeof(VuMeterControl), new PropertyMetadata(0f, OnValueChanged));

    public static readonly DependencyProperty ValueRProperty =
        DependencyProperty.Register(nameof(ValueR), typeof(float), typeof(VuMeterControl), new PropertyMetadata(0f, OnValueChanged));

    public float ValueL
    {
        get => (float)GetValue(ValueLProperty);
        set => SetValue(ValueLProperty, value);
    }

    public float ValueR
    {
        get => (float)GetValue(ValueRProperty);
        set => SetValue(ValueRProperty, value);
    }

    private static Brush TrackBrush => (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];

    public VuMeterControl()
    {
        var grid = new Grid { RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Rectangle MakeTrack()
        {
            return new Rectangle
            {
                Fill = TrackBrush,
                RadiusX = 3,
                RadiusY = 3,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
        }
        Rectangle MakeBar()
        {
            return new Rectangle
            {
                Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen),
                RadiusX = 3,
                RadiusY = 3,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 0,
            };
        }

        var trackL = MakeTrack();
        _barL = MakeBar();
        Grid.SetRow(trackL, 0);
        Grid.SetRow(_barL, 0);
        grid.Children.Add(trackL);
        grid.Children.Add(_barL);

        var trackR = MakeTrack();
        _barR = MakeBar();
        Grid.SetRow(trackR, 1);
        Grid.SetRow(_barR, 1);
        grid.Children.Add(trackR);
        grid.Children.Add(_barR);

        Content = grid;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += UpdateBars;
        _timer.Start();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VuMeterControl vm) vm.UpdateBars(null, null);
    }

    private static Color BarColorFor(float v) =>
        v > 0.9f ? Microsoft.UI.Colors.OrangeRed :
        v > 0.7f ? Microsoft.UI.Colors.Gold :
                   Microsoft.UI.Colors.LimeGreen;

    private void UpdateBars(object? sender, object? e)
    {
        double maxW = ActualWidth;
        _barL.Width = Math.Max(0, Math.Min(ValueL * maxW, maxW));
        _barR.Width = Math.Max(0, Math.Min(ValueR * maxW, maxW));

        ((SolidColorBrush)_barL.Fill).Color = BarColorFor(ValueL);
        ((SolidColorBrush)_barR.Fill).Color = BarColorFor(ValueR);
    }
}
