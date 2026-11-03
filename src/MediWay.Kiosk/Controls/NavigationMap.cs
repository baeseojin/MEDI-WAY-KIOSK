using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MediWay.Contracts;

namespace MediWay.Kiosk.Controls;

public sealed class NavigationMap : UserControl
{
    public static readonly DependencyProperty AccessibleModeProperty = DependencyProperty.Register(nameof(AccessibleMode), typeof(bool), typeof(NavigationMap),
        new PropertyMetadata(false, (owner, _) => ((NavigationMap)owner).RefreshTheme()));
    public bool AccessibleMode { get => (bool)GetValue(AccessibleModeProperty); set => SetValue(AccessibleModeProperty, value); }
    private readonly List<Button> mapButtons = [];
    private readonly TextBlock legend;
    private readonly TextBlock transferText;
    private readonly Button floorButton;
    private int legIndex;
    private NavigationRoute? displayedRoute;
    public static readonly DependencyProperty RouteProperty = DependencyProperty.Register(nameof(Route), typeof(NavigationRoute), typeof(NavigationMap),
        new PropertyMetadata(null, (owner, args) => { var map=(NavigationMap)owner;map.legIndex=0;map.Render((NavigationRoute?)args.NewValue); }));
    public NavigationRoute? Route { get => (NavigationRoute?)GetValue(RouteProperty); set => SetValue(RouteProperty, value); }
    private readonly Canvas canvas = new() { Background = Brushes.White };
    private readonly ScrollViewer scroll;
    private System.Windows.Shapes.Path? animatedPath;
    private double zoom = 1;
    private bool fit = true;
    public NavigationMap()
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var buttons = new WrapPanel();
        Button MakeButton(string label, Action action)
        {
            var button = new Button { Content = label, FontSize = 15, MinHeight = 38, Padding = new(12, 5, 12, 5), Margin = new(3), Background = Brushes.White, Foreground = Brushes.DarkSlateGray };
            button.Click += (_, _) => action(); buttons.Children.Add(button); mapButtons.Add(button); return button;
        }
        MakeButton("확대 +", () => SetZoom(zoom * 1.25)); MakeButton("축소 −", () => SetZoom(zoom / 1.25)); MakeButton("전체 보기", () => { fit = true; Fit(); });
        floorButton=MakeButton("다음 층 지도",()=>{if(Route?.Legs.Count>1){legIndex=(legIndex+1)%Route.Legs.Count;Render(Route);}});
        floorButton.Visibility=Visibility.Collapsed;
        scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.Both, Background = Brushes.White };
        Grid.SetRow(scroll, 1);
        legend = new TextBlock { Text = "청록: 이동 경로 · 주황: 목적지 · 빨간 점선: 폐쇄 통로", FontSize = 12, Foreground = Brushes.DarkSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new(4, 8, 4, 0) };
        transferText=new TextBlock {FontSize=15,TextWrapping=TextWrapping.Wrap,Margin=new(4,4,4,0)};
        var footer=new StackPanel();footer.Children.Add(legend);footer.Children.Add(transferText);
        Grid.SetRow(footer, 2); grid.Children.Add(buttons); grid.Children.Add(scroll); grid.Children.Add(footer); Content = grid;
        Loaded += (_, _) => { if (fit) Fit(); }; SizeChanged += (_, _) => { if (fit) Fit(); }; Unloaded += (_, _) => StopAnimation();
    }
    private void SetZoom(double value)
    {
        fit = false; zoom = Math.Clamp(value, 0.3, 3); canvas.LayoutTransform = new ScaleTransform(zoom, zoom);
    }
    private void Fit()
    {
        if (displayedRoute is null) return;
        zoom = Math.Clamp(Math.Min(Math.Max(100, scroll.ActualWidth - 24) / displayedRoute.Map.Width, Math.Max(100, scroll.ActualHeight - 24) / displayedRoute.Map.Height), 0.1, 3);
        canvas.LayoutTransform = new ScaleTransform(zoom, zoom); scroll.ScrollToTop(); scroll.ScrollToLeftEnd();
    }
    private void RefreshTheme()
    {
        canvas.Background = scroll.Background = AccessibleMode ? Brushes.Black : Brushes.White;
        foreach (var button in mapButtons)
        {
            button.Background = AccessibleMode ? Brushes.Black : Brushes.White;
            button.Foreground = AccessibleMode ? Brushes.White : Brushes.DarkSlateGray;
            button.FontSize = AccessibleMode ? 19 : 15; button.MinHeight = AccessibleMode ? 56 : 38;
            button.BorderBrush = AccessibleMode ? Brushes.White : Brushes.Gray;
        }
        legend.Foreground = AccessibleMode ? Brushes.White : Brushes.DarkSlateGray;
        legend.FontSize = AccessibleMode ? 16 : 12;
        transferText.Foreground=AccessibleMode?Brushes.White:Brushes.DarkSlateGray;
        legend.Text = AccessibleMode ? "노랑 선: 이동 경로 · 주황 원: 목적지 · 빨간 점선: 폐쇄 통로" : "청록: 이동 경로 · 주황: 목적지 · 빨간 점선: 폐쇄 통로";
        Render(Route);
    }
    private Brush Color(string value)
    {
        if (AccessibleMode) value = value switch
        {
            "#163947" or "#8299A3" or "#BACDD3" => "#FFFFFF",
            "#087F8C" => "#FFFF00",
            "#BC3B3B" => "#FF7777",
            "#E0E7EB" => "#666666",
            "#D8EDF1" or "#E1EEE1" or "#F7E9C9" or "#E8E5F2" => "#000000",
            _ => value
        };
        return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
    }
    private void StopAnimation() { animatedPath?.BeginAnimation(Shape.StrokeDashOffsetProperty, null); animatedPath = null; }
    private void Render(NavigationRoute? route)
    {
        StopAnimation(); canvas.Children.Clear(); displayedRoute=null;
        floorButton.Visibility=route?.Legs.Count>1?Visibility.Visible:Visibility.Collapsed;
        transferText.Text=route is null?"":route.TransferNotice;
        if (route is null) return;
        if(route.Legs.Count>0){legIndex=Math.Clamp(legIndex,0,route.Legs.Count-1);floorButton.Content=route.Legs[(legIndex+1)%route.Legs.Count].Map.Name+" 보기";route=route.Legs[legIndex];transferText.Text=route.Map.Name+" · "+transferText.Text;}
        displayedRoute=route;
        canvas.Width = route.Map.Width; canvas.Height = route.Map.Height;
        foreach (var zone in route.Map.Zones)
        {
            var fill = zone.Kind switch { "Clinic" => "#D8EDF1", "Waiting" => "#E1EEE1", "Reception" => "#F7E9C9", _ => "#E8E5F2" };
            var box = new Border
            {
                Width = zone.Width,
                Height = zone.Height,
                CornerRadius = new(8),
                Background = Color(fill),
                BorderBrush = Color("#BACDD3"),
                BorderThickness = new(1),
                Child = new TextBlock { Text = zone.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Color("#163947"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(5) }
            };
            Canvas.SetLeft(box, zone.X); Canvas.SetTop(box, zone.Y); canvas.Children.Add(box);
        }
        var nodes = route.Map.Nodes.ToDictionary(n => n.Id);
        foreach (var link in route.Map.Links)
        {
            var a = nodes[link.FromNodeId]; var b = nodes[link.ToNodeId];
            var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Color(link.IsClosed ? "#BC3B3B" : "#E0E7EB"), StrokeThickness = link.IsClosed ? 7 : 24, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            if (link.IsClosed) line.StrokeDashArray = new([2, 2]); canvas.Children.Add(line);
        }
        foreach (var node in route.Map.Nodes)
        {
            Dot(node.X, node.Y, 4, "#8299A3");
            if (node.Kind == "Entrance") Label("정문", node.X - 30, node.Y + 14, 60);
        }
        if (route.Points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(new(route.Points[0].X, route.Points[0].Y), false, false);
                drawing.PolyLineTo(route.Points.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, false);
            }
            var length = route.Points.Zip(route.Points.Skip(1), (a, b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2))).Sum() / 8;
            animatedPath = new() { Data = geometry, Stroke = Color("#087F8C"), StrokeThickness = 8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeDashArray = new([length, length]) };
            canvas.Children.Add(animatedPath);
            animatedPath.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(length, 0, TimeSpan.FromSeconds(1.4)));
        }
        var first = route.Points[0]; var last = route.Points[^1];
        Dot(first.X, first.Y, 11, "#087F8C"); Label(first.Kind=="Elevator"?"엘리베이터 하차":"현재 위치", first.X - 48, first.Y + 15, 110);
        Dot(last.X, last.Y, 11, "#DB8219"); Label(last.Kind=="Elevator"?"엘리베이터":"진료실 입구", last.X - 50, last.Y + 15, 110);
        fit = true; Fit();
    }
    private void Dot(double x, double y, double radius, string color)
    {
        var dot = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = Color(color), Stroke = Brushes.White, StrokeThickness = 2 };
        Canvas.SetLeft(dot, x - radius); Canvas.SetTop(dot, y - radius); canvas.Children.Add(dot);
    }
    private void Label(string text, double x, double y, double width)
    {
        var label = new TextBlock { Text = text, FontSize = AccessibleMode ? 22 : 18, Width = width * (AccessibleMode ? 1.25 : 1), TextAlignment = TextAlignment.Center, Foreground = Color("#163947"), Background = AccessibleMode ? Brushes.Black : Brushes.White };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); canvas.Children.Add(label);
    }
}
