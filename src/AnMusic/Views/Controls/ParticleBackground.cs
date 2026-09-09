using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AnMusic.Views.Controls;

/// <summary>
/// 动态壁纸粒子背景：自定义 FrameworkElement，使用 DrawingVisual 渲染。
/// 模式 1 = 鼠标跟随（流光），模式 2 = 星河（缓慢飘移）。
/// 由 DispatcherTimer 驱动 ~30fps；粒子颜色跟随主题强调色。
/// </summary>
public sealed class ParticleBackground : FrameworkElement
{
    /// <summary>壁纸模式：1=鼠标跟随, 2=星河。</summary>
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(int), typeof(ParticleBackground),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender, OnModeChanged));

    public int Mode
    {
        get => (int)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private readonly DrawingVisual _visual = new();
    private readonly List<Particle> _particles = new();
    private readonly DispatcherTimer _timer;
    private static readonly Random Rand = new();

    // 鼠标位置（相对于本控件）；初始置中，避免首帧 NaN
    private double _mouseX = double.NaN;
    private double _mouseY = double.NaN;

    public ParticleBackground()
    {
        AddVisualChild(_visual);
        AddLogicalChild(_visual);
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30fps
        };
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => InitParticles();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private Window? _hostWindow;

    /// <summary>可见性变化（如壁纸开关切换）时重建粒子并恢复动画，避免 Collapsed 期间宽高为 0 未初始化。</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            InitParticles();
            if (Mode > 0) _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitParticles();
        _timer.Start();
        // ParticleBackground 自身 IsHitTestVisible=False，鼠标事件透传到下层 UI；
        // 监听宿主 Window 的鼠标移动以驱动"鼠标跟随"模式。
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.MouseMove += OnHostMouseMove;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_hostWindow is not null)
        {
            _hostWindow.MouseMove -= OnHostMouseMove;
            _hostWindow = null;
        }
    }

    private void OnHostMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _mouseX = p.X;
        _mouseY = p.Y;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index)
    {
        if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
        return _visual;
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ParticleBackground pb)
        {
            pb.InitParticles();
            if (pb.Mode > 0 && pb.IsVisible) pb._timer.Start(); // 模式切换后恢复动画
        }
    }

    /// <summary>当前主题强调色（动态查找，主题切换/强调色变更后自动刷新）。</summary>
    private Color AccentColor
    {
        get
        {
            if (Application.Current.TryFindResource("Accent") is SolidColorBrush b)
                return b.Color;
            return Color.FromRgb(0x2B, 0x7D, 0xE9);
        }
    }

    private void InitParticles()
    {
        _particles.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var count = Mode switch
        {
            1 => 120, // 鼠标跟随：粒子数适中
            2 => 180, // 星河：稍多
            _ => 0
        };

        for (var i = 0; i < count; i++)
            _particles.Add(Mode == 1 ? CreateFollowParticle() : CreateStarParticle());
    }

    private Particle CreateFollowParticle()
    {
        var accent = AccentColor;
        return new Particle
        {
            X = Rand.NextDouble() * ActualWidth,
            Y = Rand.NextDouble() * ActualHeight,
            Vx = (Rand.NextDouble() - 0.5) * 0.4,
            Vy = (Rand.NextDouble() - 0.5) * 0.4,
            Size = 1.5 + Rand.NextDouble() * 2.5,
            // 跟随模式：颜色略带随机透明度的强调色
            Color = Color.FromArgb((byte)(80 + Rand.NextDouble() * 150), accent.R, accent.G, accent.B),
        };
    }

    private Particle CreateStarParticle()
    {
        var accent = AccentColor;
        // 星河：少量随机色 + 大量白色微亮，整体偏冷
        var useAccent = Rand.NextDouble() < 0.25;
        var c = useAccent
            ? Color.FromArgb((byte)(60 + Rand.NextDouble() * 130), accent.R, accent.G, accent.B)
            : Color.FromArgb((byte)(40 + Rand.NextDouble() * 140), 0xFF, 0xFF, 0xFF);
        return new Particle
        {
            X = Rand.NextDouble() * ActualWidth,
            Y = Rand.NextDouble() * ActualHeight,
            Vx = (Rand.NextDouble() - 0.5) * 0.15,
            Vy = (Rand.NextDouble() - 0.5) * 0.15,
            Size = 0.6 + Rand.NextDouble() * 2.0,
            Color = c,
        };
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        // 自愈：粒子未初始化（如初始化时宽高为 0）时按当前模式重建
        if (_particles.Count == 0 && Mode > 0) InitParticles();
        UpdateParticles();
        Render();
    }

    private void UpdateParticles()
    {
        var w = ActualWidth;
        var h = ActualHeight;

        if (Mode == 1)
        {
            // 鼠标跟随：粒子被鼠标吸引，形成流光
            var mx = double.IsNaN(_mouseX) ? w / 2 : _mouseX;
            var my = double.IsNaN(_mouseY) ? h / 2 : _mouseY;

            foreach (var p in _particles)
            {
                var dx = mx - p.X;
                var dy = my - p.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > 1)
                {
                    // 向鼠标轻微加速 + 随机抖动
                    var force = Math.Min(0.08, 8.0 / (dist + 50));
                    p.Vx += (dx / dist) * force + (Rand.NextDouble() - 0.5) * 0.05;
                    p.Vy += (dy / dist) * force + (Rand.NextDouble() - 0.5) * 0.05;
                }
                // 阻尼，避免速度无限增长
                p.Vx *= 0.96;
                p.Vy *= 0.96;
                p.X += p.Vx;
                p.Y += p.Vy;

                // 边界环绕
                if (p.X < 0) p.X += w; else if (p.X > w) p.X -= w;
                if (p.Y < 0) p.Y += h; else if (p.Y > h) p.Y -= h;
            }
        }
        else
        {
            // 星河：缓慢飘移
            foreach (var p in _particles)
            {
                p.X += p.Vx;
                p.Y += p.Vy;
                if (p.X < 0) p.X += w; else if (p.X > w) p.X -= w;
                if (p.Y < 0) p.Y += h; else if (p.Y > h) p.Y -= h;
            }
        }
    }

    private void Render()
    {
        var dc = _visual.RenderOpen();
        try
        {
            // 半透明清屏，制造拖尾余晖
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(8, 0, 0, 0)), null,
                new Rect(0, 0, ActualWidth, ActualHeight));

            if (Mode == 1)
            {
                // 鼠标跟随模式：先画粒子间连线（流光），再画粒子
                var accent = AccentColor;
                var linePen = new Pen(new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)), 1);
                linePen.Freeze();
                for (var i = 0; i < _particles.Count; i++)
                {
                    var a = _particles[i];
                    for (var j = i + 1; j < _particles.Count; j++)
                    {
                        var b = _particles[j];
                        var dx = a.X - b.X;
                        var dy = a.Y - b.Y;
                        var d2 = dx * dx + dy * dy;
                        if (d2 < 60 * 60) // 连线半径
                        {
                            dc.DrawLine(linePen, new Point(a.X, a.Y), new Point(b.X, b.Y));
                        }
                    }
                }
            }

            foreach (var p in _particles)
            {
                var brush = new SolidColorBrush(p.Color);
                brush.Freeze();
                var r = p.Size;
                dc.DrawEllipse(brush, null, new Point(p.X, p.Y), r, r);
            }
        }
        finally
        {
            dc.Close();
        }
    }

    private sealed class Particle
    {
        public double X, Y;
        public double Vx, Vy;
        public double Size;
        public Color Color;
    }
}
