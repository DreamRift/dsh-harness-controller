// ============================================================================
//  UsageTrendChart — Cockpit Tools CodexUsageTrend 等价的原生 WinUI 绘制
//  静态曲线与悬浮层分离，鼠标移动不会重新绘制/闪烁主曲线。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using DshController.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace DshController.Views
{
    public sealed partial class UsageTrendChart : UserControl
    {
        public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
            nameof(Points), typeof(IEnumerable<UsageTrendPoint>), typeof(UsageTrendChart),
            new PropertyMetadata(null, OnInputsChanged));
        public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
            nameof(Metric), typeof(string), typeof(UsageTrendChart),
            new PropertyMetadata("tokens", OnInputsChanged));

        private const double Left = 58, Right = 18, Top = 28, Bottom = 58;
        private INotifyCollectionChanged _notifier;
        private List<UsageTrendPoint> _points = new List<UsageTrendPoint>();
        private readonly List<Point> _positions = new List<Point>();
        private int _activeIndex = -1;
        private bool _rendering;
        private bool _hasRendered;

        public IEnumerable<UsageTrendPoint> Points
        {
            get => (IEnumerable<UsageTrendPoint>)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        public string Metric
        {
            get => (string)GetValue(MetricProperty);
            set => SetValue(MetricProperty, value);
        }

        public UsageTrendChart() => InitializeComponent();

        private static void OnInputsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var chart = (UsageTrendChart)sender;
            if (args.Property == PointsProperty)
            {
                if (chart._notifier != null) chart._notifier.CollectionChanged -= chart.PointsChanged;
                chart._notifier = args.NewValue as INotifyCollectionChanged;
                if (chart._notifier != null) chart._notifier.CollectionChanged += chart.PointsChanged;
            }
            chart.RebuildStatic();
        }

        private void PointsChanged(object sender, NotifyCollectionChangedEventArgs e) => RebuildStatic();
        private void StaticCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildStatic();

        private void RebuildStatic()
        {
            if (_rendering || StaticCanvas == null || StaticCanvas.ActualWidth < 80 || StaticCanvas.ActualHeight < 80) return;
            _rendering = true;
            try
            {
                _points = (Points ?? Enumerable.Empty<UsageTrendPoint>()).ToList();
                StaticCanvas.Children.Clear();
                _positions.Clear();
                EmptyText.Visibility = _points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                HoverCard.Visibility = Visibility.Collapsed;
                if (_points.Count == 0) { _activeIndex = -1; return; }

                double width = StaticCanvas.ActualWidth;
                double height = StaticCanvas.ActualHeight;
                double xSpan = Math.Max(1, width - Left - Right);
                double ySpan = Math.Max(1, height - Top - Bottom);
                List<double> values = _points.Select(ValueFor).ToList();
                // 先在原始值域求 Cockpit Catmull-Rom 控制点的完整范围；不裁剪，
                // 曲线的真实平滑过冲也会随自动纵轴完整显示在时间轴上方。
                List<double> domain = CatmullRomDomain(values);
                double min = domain.Min();
                double max = domain.Max();
                // Cockpit CodexUsageTrend：3%/6% 留白，平值曲线也保持视觉高度。
                double span = Math.Max(Math.Max(max - min, max * .08), 1);
                for (int index = 0; index < _points.Count; index++)
                {
                    double x = Left + (_points.Count == 1 ? xSpan / 2 : index * xSpan / (_points.Count - 1));
                    double y = Top + (1 - (values[index] - min + span * .03) / (span * 1.06)) * ySpan;
                    _positions.Add(new Point(x, y));
                }

                DrawGrid(width, height, ySpan);
                DrawArea(height);
                DrawLine();
                DrawLabels(width, height);
                RenderInteraction();
                if (!_hasRendered) AnimateStatic();
                else StaticCanvas.Opacity = 1;
                _hasRendered = true;
            }
            finally { _rendering = false; }
        }

        private double ValueFor(UsageTrendPoint point) => Metric == "requests" ? point.Requests : point.Tokens;

        private void DrawGrid(double width, double height, double ySpan)
        {
            for (int index = 0; index < 3; index++)
            {
                double y = Top + index * ySpan / 2;
                StaticCanvas.Children.Add(new Line
                {
                    X1 = Left, X2 = width - Right, Y1 = y, Y2 = y,
                    Stroke = Brush(88, 120, 151, 184), StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 7 }, IsHitTestVisible = false
                });
            }
            StaticCanvas.Children.Add(new Line
            {
                X1 = Left, X2 = width - Right, Y1 = height - Bottom, Y2 = height - Bottom,
                Stroke = Brush(140, 120, 151, 184), StrokeThickness = 1, IsHitTestVisible = false
            });
        }

        private void DrawArea(double height)
        {
            double baseline = height - Bottom;
            PathFigure figure = BuildSmoothFigure(_positions, true, baseline);
            if (figure == null) return;
            var geometry = new PathGeometry(); geometry.Figures.Add(figure);
            var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fill.GradientStops.Add(new GradientStop { Color = Color.FromArgb(66, 65, 134, 237), Offset = 0 });
            fill.GradientStops.Add(new GradientStop { Color = Color.FromArgb(5, 65, 134, 237), Offset = 1 });
            StaticCanvas.Children.Add(new Path { Data = geometry, Fill = fill, IsHitTestVisible = false });
        }

        private void DrawLine()
        {
            PathFigure figure = BuildSmoothFigure(_positions, false, 0);
            if (figure == null) return;
            var geometry = new PathGeometry(); geometry.Figures.Add(figure);
            StaticCanvas.Children.Add(new Path
            {
                Data = geometry, Stroke = Brush(255, 65, 134, 237), StrokeThickness = 3.5,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
            });
        }

        private static List<double> CatmullRomDomain(IReadOnlyList<double> values)
        {
            var domain = new List<double>(values);
            for (int index = 0; index < values.Count - 1; index++)
            {
                double previous = values[Math.Max(0, index - 1)];
                double current = values[index];
                double next = values[index + 1];
                double afterNext = values[Math.Min(values.Count - 1, index + 2)];
                domain.Add(current + (next - previous) / 6);
                domain.Add(next - (afterNext - current) / 6);
            }
            return domain;
        }

        // Cockpit 的 buildSmoothPath：标准 Catmull-Rom 等价三次 Bézier 控制点。
        private static PathFigure BuildSmoothFigure(IReadOnlyList<Point> points, bool area, double baseline)
        {
            if (points.Count == 0) return null;
            var figure = new PathFigure { StartPoint = points[0], IsClosed = area };
            if (points.Count == 1)
            {
                if (area) figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, baseline) });
                return figure;
            }
            for (int index = 0; index < points.Count - 1; index++)
            {
                Point current = points[index];
                Point next = points[index + 1];
                Point previous = points[Math.Max(0, index - 1)];
                Point afterNext = points[Math.Min(points.Count - 1, index + 2)];
                figure.Segments.Add(new BezierSegment
                {
                    Point1 = new Point(current.X + (next.X - previous.X) / 6, current.Y + (next.Y - previous.Y) / 6),
                    Point2 = new Point(next.X - (afterNext.X - current.X) / 6, next.Y - (afterNext.Y - current.Y) / 6),
                    Point3 = next
                });
            }
            if (area)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(points[points.Count - 1].X, baseline) });
                figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, baseline) });
            }
            return figure;
        }

        private void DrawLabels(double width, double height)
        {
            var occupied = new List<Tuple<double, double>>();
            foreach (int index in Enumerable.Range(0, _points.Count).Where(i => ValueFor(_points[i]) > 0).OrderByDescending(i => ValueFor(_points[i])))
            {
                string text = Metric == "requests" ? _points[index].RequestsText : _points[index].TokensText;
                double labelWidth = Math.Max(42, Math.Min(112, text.Length * 7 + 16));
                double left = Math.Max(Left, Math.Min(width - Right - labelWidth, _positions[index].X - labelWidth / 2));
                if (occupied.Any(range => left < range.Item2 + 4 && left + labelWidth > range.Item1 - 4)) continue;
                occupied.Add(Tuple.Create(left, left + labelWidth));
                var badge = new Border
                {
                    Width = labelWidth, Height = 20, CornerRadius = new CornerRadius(7),
                    Background = Brush(42, 65, 134, 237), BorderBrush = Brush(130, 65, 134, 237), BorderThickness = new Thickness(1),
                    IsHitTestVisible = false,
                    Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush(255, 65, 134, 237), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                Canvas.SetLeft(badge, left); Canvas.SetTop(badge, Math.Max(Top + 3, _positions[index].Y - 29)); StaticCanvas.Children.Add(badge);
            }
            int step = Math.Max(1, (int)Math.Ceiling(_points.Count / 7.0));
            for (int index = 0; index < _points.Count; index++)
            {
                if (index % step != 0 && index != _points.Count - 1) continue;
                var label = new TextBlock { Text = _points[index].Label, FontSize = 10.5, Foreground = Brush(210, 168, 190, 216), IsHitTestVisible = false };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, Math.Max(0, Math.Min(width - label.DesiredSize.Width, _positions[index].X - label.DesiredSize.Width / 2)));
                Canvas.SetTop(label, height - 30); StaticCanvas.Children.Add(label);
            }
        }

        private void RenderInteraction()
        {
            if (InteractionCanvas == null) return;
            InteractionCanvas.Children.Clear();
            if (_points.Count == 0 || _positions.Count == 0) return;
            double height = InteractionCanvas.ActualHeight;
            if (_activeIndex >= 0 && _activeIndex < _positions.Count) DrawActiveGuide(height);
            for (int index = 0; index < _positions.Count; index++)
            {
                if (ValueFor(_points[index]) <= 0) continue;
                bool active = index == _activeIndex;
                var point = new Ellipse { Width = active ? 10 : 7, Height = active ? 10 : 7, Fill = Brush(255, 28, 42, 60), Stroke = Brush(255, 65, 134, 237), StrokeThickness = active ? 3 : 2, IsHitTestVisible = false };
                Canvas.SetLeft(point, _positions[index].X - point.Width / 2); Canvas.SetTop(point, _positions[index].Y - point.Height / 2); InteractionCanvas.Children.Add(point);
            }
            if (_activeIndex >= 0) SetHoverCard(_activeIndex);
        }

        private void DrawActiveGuide(double height)
        {
            Point point = _positions[_activeIndex];
            var slice = new Rectangle { Width = 36, Height = height - Top - Bottom, Fill = Brush(18, 65, 134, 237), IsHitTestVisible = false };
            Canvas.SetLeft(slice, point.X - 18); Canvas.SetTop(slice, Top); InteractionCanvas.Children.Add(slice);
            InteractionCanvas.Children.Add(new Line { X1 = point.X, X2 = point.X, Y1 = Top, Y2 = height - Bottom, Stroke = Brush(170, 65, 134, 237), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 }, IsHitTestVisible = false });
        }

        private void InteractionCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_points.Count == 0) return;
            Point pointer = e.GetCurrentPoint(InteractionCanvas).Position;
            double plotRight = InteractionCanvas.ActualWidth - Right;
            double baseline = InteractionCanvas.ActualHeight - Bottom;
            if (pointer.X < Left || pointer.X > plotRight || pointer.Y < Top || pointer.Y > baseline)
            {
                HideHover();
                return;
            }
            double fraction = (pointer.X - Left) / Math.Max(1, plotRight - Left);
            ShowHover((int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * (_points.Count - 1)));
        }

        private void InteractionCanvas_PointerExited(object sender, PointerRoutedEventArgs e) => HideHover();
        private void InteractionCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_activeIndex < 0 || _activeIndex >= _points.Count) return;
            _points[_activeIndex].SelectCommand?.Execute(null);
            e.Handled = true;
        }

        private void ShowHover(int index)
        {
            if (index < 0 || index >= _points.Count || index == _activeIndex) return;
            _activeIndex = index;
            RenderInteraction();
        }

        private void HideHover()
        {
            if (_activeIndex < 0) return;
            _activeIndex = -1;
            HoverCard.Visibility = Visibility.Collapsed;
            RenderInteraction();
        }

        private void SetHoverCard(int index)
        {
            UsageTrendPoint point = _points[index];
            Point position = _positions[index];
            HoverTitle.Text = point.Label; HoverTokens.Text = "总 Token 数  " + point.TokensText;
            HoverRequests.Text = "总请求数  " + point.RequestsText; HoverTtft.Text = "平均首 Token  " + point.TtftText;
            HoverCard.Visibility = Visibility.Visible;
            Canvas.SetLeft(HoverCard, Math.Max(Left, Math.Min(OverlayCanvas.ActualWidth - HoverCard.Width - Right, position.X - HoverCard.Width / 2)));
            Canvas.SetTop(HoverCard, 8);
            HoverCard.Opacity = 1;
        }

        private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_points.Count == 0) return;
            if (e.Key == Windows.System.VirtualKey.Left) { ShowHover(Math.Max(0, _activeIndex - 1)); e.Handled = true; }
            if (e.Key == Windows.System.VirtualKey.Right) { ShowHover(Math.Min(_points.Count - 1, _activeIndex + 1)); e.Handled = true; }
            if (e.Key == Windows.System.VirtualKey.Enter && _activeIndex >= 0) { _points[_activeIndex].SelectCommand?.Execute(null); e.Handled = true; }
        }

        private void AnimateStatic()
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) { StaticCanvas.Opacity = 1; return; }
            StaticCanvas.Opacity = 0;
            var story = new Storyboard();
            story.Children.Add(new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(260)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Storyboard.SetTarget(story.Children[0], StaticCanvas); Storyboard.SetTargetProperty(story.Children[0], "Opacity"); story.Begin();
        }

        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
