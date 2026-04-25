using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SonosStreaming.App.Controls;

public sealed class SpectrumControl : UserControl
{
    private readonly CanvasControl _canvas;
    private readonly DispatcherTimer _timer;

    public static readonly DependencyProperty LevelsProperty =
        DependencyProperty.Register(nameof(Levels), typeof(float[]), typeof(SpectrumControl), new PropertyMetadata(Array.Empty<float>(), OnLevelsChanged));

    public float[] Levels
    {
        get => (float[])GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public SpectrumControl()
    {
        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        Content = _canvas;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => _canvas.Invalidate();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private static void OnLevelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpectrumControl sc) sc._canvas?.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var levels = Levels;
        if (levels == null || levels.Length == 0) return;

        float w = (float)sender.ActualWidth;
        float h = (float)sender.ActualHeight;
        int bands = levels.Length;
        float gap = 2f;
        float barWidth = MathF.Max(1f, w / bands - gap);

        using var ds = args.DrawingSession;
        for (int i = 0; i < bands; i++)
        {
            float level = MathF.Min(1f, levels[i]);
            float barHeight = level * h;
            float x = i * (barWidth + gap);
            var color = level > 0.95f ? Microsoft.UI.Colors.OrangeRed
                      : level > 0.85f ? Microsoft.UI.Colors.Gold
                                      : Microsoft.UI.Colors.DodgerBlue;
            ds.FillRectangle(x, h - barHeight, barWidth, barHeight, color);
        }
    }
}
