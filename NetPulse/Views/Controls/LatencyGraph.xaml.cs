using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetPulse.Models;

namespace NetPulse.Views.Controls;

public partial class LatencyGraph : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable<PingPoint>),
            typeof(LatencyGraph),
            new PropertyMetadata(null, OnItemsSourceChanged));

    private INotifyCollectionChanged? _currentNotifySource;

    private sealed record RenderedDot(Point Point, PingPoint Sample);

    private readonly List<RenderedDot> _renderedDots = new();
    private RenderedDot? _activeDot;

    private const double DotDiameter = 7.0;
    private const double TooltipSnapRadiusPx = 30.0;

    // Light gray shading for gaps (semi-transparent)
    private static readonly Brush GapBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    public IEnumerable<PingPoint>? ItemsSource
    {
        get => (IEnumerable<PingPoint>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public LatencyGraph()
    {
        InitializeComponent();

        Loaded += (_, _) => Render();
        SizeChanged += (_, _) => Render();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LatencyGraph)d;
        control.DetachCollectionChanged();
        control.AttachCollectionChanged(e.NewValue);
        control.HideTooltip();
        control.Render();
    }

    private void AttachCollectionChanged(object? source)
    {
        _currentNotifySource = source as INotifyCollectionChanged;
        if (_currentNotifySource is not null)
            _currentNotifySource.CollectionChanged += OnCollectionChanged;
    }

    private void DetachCollectionChanged()
    {
        if (_currentNotifySource is not null)
            _currentNotifySource.CollectionChanged -= OnCollectionChanged;

        _currentNotifySource = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HideTooltip();
        Render();
    }

    private void Render()
    {
        if (!IsLoaded)
            return;

        var width = GraphHost.ActualWidth;
        var height = GraphHost.ActualHeight;

        if (width < 10 || height < 10)
            return;

        GapCanvas.Children.Clear();
        DotsCanvas.Children.Clear();
        _renderedDots.Clear();
        HideTooltip();

        var points = ItemsSource?.ToList() ?? new List<PingPoint>();
        if (points.Count < 2)
        {
            GraphPath.Data = null;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        // Valid samples: LatencyMs >= 0
        var valid = points.Where(p => p.LatencyMs >= 0).ToList();
        if (valid.Count < 2)
        {
            GraphPath.Data = null;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        var minLat = valid.Min(p => p.LatencyMs);
        var maxLat = valid.Max(p => p.LatencyMs);
        if (minLat == maxLat)
            maxLat = minLat + 10;

        // Add 10% padding
        var range = maxLat - minLat;
        var paddedMin = Math.Max(0, minLat - range * 0.10);
        var paddedMax = maxLat + range * 0.10;

        var minT = points.Min(p => p.Timestamp);
        var maxT = points.Max(p => p.Timestamp);
        var totalMs = (maxT - minT).TotalMilliseconds;
        var useIndexX = totalMs <= 0;

        double MapX(PingPoint p, int index)
        {
            return useIndexX
                ? (points.Count == 1 ? 0 : (index / (double)(points.Count - 1)) * width)
                : ((p.Timestamp - minT).TotalMilliseconds / totalMs) * width;
        }

        Point MapPoint(PingPoint p, int index)
        {
            var x = MapX(p, index);
            var y = height - ((p.LatencyMs - paddedMin) / (paddedMax - paddedMin) * height);
            return new Point(x, y);
        }

        // 1) Gap shading
        bool inGap = false;
        double gapStartX = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var isGap = p.LatencyMs < 0;
            var x = MapX(p, i);

            if (isGap && !inGap)
            {
                inGap = true;
                gapStartX = x;
            }
            else if (!isGap && inGap)
            {
                inGap = false;
                AddGapRect(gapStartX, x);
            }
        }

        if (inGap)
        {
            // Extend to end if the series ends in a gap
            AddGapRect(gapStartX, width);
        }

        void AddGapRect(double xStart, double xEnd)
        {
            var left = Math.Max(0, Math.Min(xStart, xEnd));
            var right = Math.Min(width, Math.Max(xStart, xEnd));
            var w = Math.Max(1, right - left);

            var rect = new Rectangle
            {
                Width = w,
                Height = height,
                Fill = GapBrush
            };

            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, 0);
            GapCanvas.Children.Add(rect);
        }

        // 2) Line geometry with gaps
        var geometry = new PathGeometry();
        List<Point>? currentSegment = null;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];

            // Gap marker
            if (p.LatencyMs < 0)
            {
                FlushSegment();
                continue;
            }

            currentSegment ??= new List<Point>();
            currentSegment.Add(MapPoint(p, i));
        }

        FlushSegment();
        GraphPath.Data = geometry;

        void FlushSegment()
        {
            if (currentSegment is null)
                return;

            if (currentSegment.Count >= 2)
            {
                var fig = new PathFigure
                {
                    StartPoint = currentSegment[0],
                    IsClosed = false,
                    IsFilled = false
                };

                var seg = new PolyLineSegment();
                for (var j = 1; j < currentSegment.Count; j++)
                    seg.Points.Add(currentSegment[j]);

                fig.Segments.Add(seg);
                geometry.Figures.Add(fig);
            }

            currentSegment = null;
        }

        // 3) Dots (every valid point)
        var dotBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.DeepSkyBlue;
        var dotStroke = TryFindResource("BorderBrush") as Brush ?? Brushes.Transparent;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.LatencyMs < 0)
                continue;

            var pt = MapPoint(p, i);

            var ellipse = new Ellipse
            {
                Width = DotDiameter,
                Height = DotDiameter,
                Fill = dotBrush,
                Stroke = dotStroke,
                StrokeThickness = 1
            };

            Canvas.SetLeft(ellipse, pt.X - DotDiameter / 2.0);
            Canvas.SetTop(ellipse, pt.Y - DotDiameter / 2.0);

            DotsCanvas.Children.Add(ellipse);
            _renderedDots.Add(new RenderedDot(pt, p));
        }
    }

    private void GraphHost_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => HideTooltip();

    private void GraphHost_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_renderedDots.Count == 0)
        {
            HideTooltip();
            return;
        }

        var pos = e.GetPosition(GraphHost);

        RenderedDot? nearest = null;
        var bestDist2 = double.MaxValue;

        foreach (var d in _renderedDots)
        {
            var dx = d.Point.X - pos.X;
            var dy = d.Point.Y - pos.Y;
            var dist2 = dx * dx + dy * dy;

            if (dist2 < bestDist2)
            {
                bestDist2 = dist2;
                nearest = d;
            }
        }

        var snap2 = TooltipSnapRadiusPx * TooltipSnapRadiusPx;
        if (nearest is null || bestDist2 > snap2)
        {
            HideTooltip();
            return;
        }

        if (!Equals(_activeDot, nearest))
            ShowTooltip(nearest);
        else
            PositionTooltip(nearest);
    }

    private void ShowTooltip(RenderedDot dot)
    {
        _activeDot = dot;

        var time = dot.Sample.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        TooltipText.Text = $"{dot.Sample.LatencyMs} ms • {time}";

        TooltipBubble.Visibility = Visibility.Visible;
        TooltipBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        PositionTooltip(dot);
    }

    private void PositionTooltip(RenderedDot dot)
    {
        if (TooltipBubble.Visibility != Visibility.Visible)
            return;

        TooltipBubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = TooltipBubble.DesiredSize;

        var width = GraphHost.ActualWidth;
        var height = GraphHost.ActualHeight;

        var x = dot.Point.X - size.Width / 2.0;
        x = Math.Max(0, Math.Min(width - size.Width, x));

        var y = dot.Point.Y - size.Height - 10;
        y = Math.Max(0, Math.Min(height - size.Height, y));

        Canvas.SetLeft(TooltipBubble, x);
        Canvas.SetTop(TooltipBubble, y);
    }

    private void HideTooltip()
    {
        _activeDot = null;
        TooltipBubble.Visibility = Visibility.Collapsed;
    }
}