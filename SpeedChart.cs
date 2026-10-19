using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace ModelDesk;

public sealed class SpeedChart : UserControl
{
    private readonly record struct Sample(DateTimeOffset Time, double? Value);
    private readonly Canvas canvas = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Queue<Sample> tokens = new();
    private readonly Queue<Sample> gpu = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly UISettings uiSettings = new();
    private static readonly TimeSpan TimeWindow = TimeSpan.FromSeconds(120);

    public SpeedChart()
    {
        Content = canvas;
        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
        Loaded += (_, _) => Draw();
        // Some unpackaged Windows configurations reject the legacy UWP accessibility
        // event subscription (HRESULT 0x80070490). WinUI theme events and incoming
        // telemetry already redraw the chart; do not let an optional hook crash startup.
    }
    public void Add(double? value, DateTimeOffset? timestamp = null) => AddSample(tokens, value, timestamp, false);
    public void AddGpu(double? value, DateTimeOffset? timestamp = null) => AddSample(gpu, value, timestamp, true);

    public void CopyHistoryTo(SpeedChart target)
    {
        if (ReferenceEquals(this, target)) return;
        target.tokens.Clear(); target.gpu.Clear();
        foreach (var item in tokens) target.tokens.Enqueue(item);
        foreach (var item in gpu) target.gpu.Enqueue(item);
        target.Draw();
    }

    private void AddSample(Queue<Sample> series, double? value, DateTimeOffset? timestamp, bool percentage)
    {
        DateTimeOffset time = (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime();
        bool valid = value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 && (!percentage || value.Value <= 100);
        series.Enqueue(new Sample(time, valid ? value : null));
        while (series.Count > 256) series.Dequeue();
        Prune(tokens, time); Prune(gpu, time);
        Draw();
    }
    private static void Prune(Queue<Sample> series, DateTimeOffset now)
    {
        while (series.Count > 0 && now - series.Peek().Time > TimeWindow) series.Dequeue();
    }
    private void HighContrastChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(Draw);

    private void Draw()
    {
        canvas.Children.Clear();
        double width = ActualWidth, height = ActualHeight;
        if (width < 140 || height < 64) return;
        bool highContrast = accessibility.HighContrast;
        var foreground = new SolidColorBrush(highContrast ? uiSettings.UIElementColor(UIElementType.WindowText) :
            ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var accent = new SolidColorBrush(highContrast ? uiSettings.UIElementColor(UIElementType.Highlight) : uiSettings.GetColorValue(UIColorType.Accent));
        double left = 38, right = width - 34, top = 30, bottom = height - 22;
        double plotWidth = right - left, plotHeight = bottom - top;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset start = now - TimeWindow;
        double max = Math.Max(10, tokens.Where(x => x.Time >= start && x.Value.HasValue).Select(x => x.Value!.Value).DefaultIfEmpty(0).Max() * 1.15);

        canvas.Children.Add(new Line { X1 = left, X2 = left + 18, Y1 = 10, Y2 = 10, Stroke = accent, StrokeThickness = 2 });
        Label("token/s", left + 24, 0, accent);
        canvas.Children.Add(new Line { X1 = right - 62, X2 = right - 44, Y1 = 10, Y2 = 10, Stroke = foreground, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } });
        Label("GPU %", right - 38, 0, foreground);
        for (int i = 0; i <= 2; i++)
        {
            double y = top + plotHeight * i / 2;
            canvas.Children.Add(new Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = foreground, Opacity = highContrast ? .55 : .16, StrokeThickness = 1 });
            Label((max * (2 - i) / 2).ToString("0.#"), 0, y - 8, foreground);
            Label((100 - i * 50).ToString(), right + 6, y - 8, foreground);
        }
        Label("−120s", left - 12, bottom + 4, foreground);
        Label("−60s", left + plotWidth / 2 - 14, bottom + 4, foreground);
        Label("现在", right - 20, bottom + 4, foreground);
        Plot(tokens, max, accent, false, TimeSpan.FromSeconds(15));
        Plot(gpu, 100, foreground, true, TimeSpan.FromSeconds(7));

        void Plot(Queue<Sample> series, double scale, Brush brush, bool dashed, TimeSpan gap)
        {
            Polyline? line = null;
            DateTimeOffset? previous = null;
            foreach (Sample sample in series)
            {
                if (sample.Time < start || sample.Time > now || !sample.Value.HasValue)
                {
                    line = null; previous = null; continue;
                }
                if (previous.HasValue && (sample.Time <= previous.Value || sample.Time - previous.Value > gap)) line = null;
                if (line == null)
                {
                    line = new Polyline { Stroke = brush, StrokeThickness = 2 };
                    if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 2 };
                    canvas.Children.Add(line);
                }
                double x = left + (sample.Time - start).TotalSeconds / TimeWindow.TotalSeconds * plotWidth;
                double y = bottom - sample.Value.Value / scale * plotHeight;
                line.Points.Add(new Point(x, y));
                var dot = new Ellipse { Width = 3, Height = 3, Fill = brush };
                Canvas.SetLeft(dot, x - 1.5); Canvas.SetTop(dot, y - 1.5); canvas.Children.Add(dot);
                previous = sample.Time;
            }
        }
    }
    private void Label(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = 10, IsHitTestVisible = false };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); canvas.Children.Add(label);
    }
}
