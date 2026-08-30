using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PriceIndicator;

/// <summary>
/// 随机数选取器页面：从设置中导入的字典里每 0.1s 随机抽取一个字符显示在气泡内。
/// 字符中心随机落在卡片内（可溢出显示不完全），字符可重叠；每个字符独立生命周期
/// （0.1s 出现 alpha 0→100，0.3s 停留 alpha=100，0.1s 消亡 alpha 100→0，共 0.5s）。
/// 长按页面随机选中一个「不在消亡周期」的字符激活（alpha 随按住时长 100→255，满 1s 完全激活）：
/// 激活中生命周期照算但锁定存活（走完消亡不移除）；完全激活后位移到气泡中心拼入字符串并居中排列，
/// 随后无缝衔接激活下一个；松手时若在出现 / 停留周期则恢复原生命周期，若已在消亡周期则 0.05s 加速消亡。
/// 三击页面清空所有字符并用 banner「重置」平滑过渡。
/// </summary>
public partial class BubbleWindow
{
    private bool _raffleActive;
    private readonly DispatcherTimer _raffleTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };

    private readonly List<RaffleChar> _chars = new();      // 全部存活字符（含已激活 / 锁定）
    private readonly List<RaffleChar> _activated = new();  // 已完全激活、拼入中心字符串的字符
    private List<string> _dictionary = new();              // 字典字符池（按 Unicode 代码点）
    private double _spawnAccum;
    private DateTime _lastFrameAt = DateTime.Now;

    // 长按激活状态
    private bool _pressing;
    private RaffleChar? _target;          // 当前激活目标（按住期间最多一个）
    private DateTime _targetStartAt;      // 目标选中时刻（激活进度计时起点）
    private double _fastDieFromAlpha;     // 快速消亡起始 alpha

    // 注意：RaffleBrand 必须声明在 RaffleBrush 之前 —— 静态字段按文本顺序初始化，
    // 否则 CreateRaffleBrush 读到的 RaffleBrand 还是 default(Color)（全透明），字符永远看不见
    private static readonly Color RaffleBrand = Color.FromRgb(0x39, 0x64, 0xFE);
    private static readonly Brush RaffleBrush = CreateRaffleBrush();
    private static readonly Typeface RaffleTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private const double SpawnIntervalMs = 100;  // 每 0.1s 抽一个字符
    private const double AppearMs = 100;         // 出现 0.1s：alpha 0→100
    private const double HoldEndMs = 400;        // 停留至 0.4s：alpha=100
    private const double DieMs = 100;            // 消亡 0.1s：alpha 100→0（总生命周期 0.5s）
    private const double FastDieMs = 50;         // 松手加速消亡 0.05s
    private const double ActivateSeconds = 1.0;  // 长按满 1s 完全激活
    private const double CharFontSize = 30;
    private const double ActivatedGap = 2;       // 已激活字符间距（px）

    private sealed class RaffleChar
    {
        public required string Text;
        public required TextBlock Block;
        public required double Width;   // 测量宽度
        public required double Height;  // 测量高度
        public double X, Y;             // 当前中心位置
        public double TargetX, TargetY; // 目标中心位置
        public required DateTime BornAt;
        public bool Activated;          // 已完全激活（永久保留）
        public bool DyingFast;          // 快速消亡中
        public DateTime FastDieStart;
        public double Alpha;            // 当前显示 alpha（0-255）
    }

    private enum RafflePhase { Appear, Hold, Die, Expired }

    /// <summary>构造时调用：接线面板尺寸变化 → 通知宿主重排定位。</summary>
    private void WireRaffleFeature()
    {
        RafflePanel.SizeChanged += (_, _) => RequestHostLayout();
        RaffleCard.MouseLeftButtonDown += OnRaffleMouseDown;
        RaffleCard.MouseLeftButtonUp += OnRaffleMouseUp;
        _raffleTimer.Tick += (_, _) => TickRaffle();
    }

    /// <summary>切换随机数选取器模式：开启时气泡变为随机字符池界面，关闭时恢复普通气泡。</summary>
    public void ToggleRaffle(bool enable)
    {
        if (enable && _usageActive) ToggleUsage(false); // 与用量查询互斥
        if (enable && _bongoActive) ToggleBongo(false); // 与手鼓猫互斥
        if (enable && _audioActive) ToggleAudio(false); // 与音频监听互斥
        if (enable && _clockActive) ToggleClock(false); // 与桌面时钟互斥
        if (enable == _raffleActive) return;
        _raffleActive = enable;
        if (Host is { } host) host.RaffleMenuItem.IsChecked = enable;
        ++_bannerGeneration; // 作废进行中的 banner 动画，避免关闭后残留

        if (enable)
        {
            // 进入随机数界面：旧页面快速淡出、随机数面板快速淡入（尺寸过渡见 TransitionToPage），暂停峰谷刷新
            _timer.Stop();
            TransitionToPage(RafflePanel);

            BuildRaffleDictionary();
            if (_dictionary.Count > 0)
            {
                RaffleHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                // 提示标语显示当前字符数，便于确认字典是否已生效
                RaffleHint.Text = "请在设置中导入字典\n（当前 0 个字符）";
                RaffleHint.Visibility = Visibility.Visible;
            }

            _pressing = false;
            _target = null;
            _spawnAccum = 0;
            _lastFrameAt = DateTime.Now;
            _raffleTimer.Start();

            // 入场动画：轻微缩放（淡入由页面过渡统一负责）
            RafflePanelScale.ScaleX = 0.92;
            RafflePanelScale.ScaleY = 0.92;
            var scale = new DoubleAnimation(0.92, 1, TimeSpan.FromSeconds(0.22))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            RafflePanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            RafflePanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

            RequestHostLayout();
        }
        else
        {
            // 恢复正常气泡：随机数面板淡出、气泡淡入，右/下边缘钉住、左/上边缘滑回
            _raffleTimer.Stop();
            _pressing = false;
            _target = null;
            ClearRaffleChars();
            TransitionToPage(Badge);
            _timer.Start();
            Refresh();
            RequestHostLayout();
        }
    }

    // ---------- 字典 ----------

    /// <summary>从配置的字典文本构建字符池（按 Unicode 代码点，跳过空白；异常时按空池处理）。</summary>
    private void BuildRaffleDictionary()
    {
        _dictionary = new List<string>();
        try
        {
            string dict = "test123456";//CurrentConfig.RaffleDictionary ?? "";
            Console.WriteLine(dict);
            foreach (var rune in dict.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune)) continue;
                _dictionary.Add(rune.ToString());
            }
        }
        catch
        {
            _dictionary = new List<string>(); // 非法文本视为空字典
        }
    }

    private string RandomChar()
        => _dictionary[Random.Shared.Next(_dictionary.Count)];

    // ---------- 生成 / 生命周期 ----------

    /// <summary>每帧驱动：生成新字符、更新生命周期 alpha 与位置、推进激活进度。</summary>
    private void TickRaffle()
    {
        if (!_raffleActive) return;

        var now = DateTime.Now;
        double dt = Math.Min((now - _lastFrameAt).TotalMilliseconds, 200);
        _lastFrameAt = now;

        double cw = RaffleCanvas.ActualWidth;
        double ch = RaffleCanvas.ActualHeight;

        // 每 0.1s 随机生成一个新字符
        if (cw > 0 && ch > 0 && _dictionary.Count > 0)
        {
            _spawnAccum += dt;
            while (_spawnAccum >= SpawnIntervalMs)
            {
                _spawnAccum -= SpawnIntervalMs;
                SpawnRaffleChar(cw, ch);
            }
        }

        // 更新全部字符
        for (int i = _chars.Count - 1; i >= 0; i--)
        {
            var c = _chars[i];
            bool remove = false;
            double a;

            if (c.DyingFast)
            {
                double t = (now - c.FastDieStart).TotalMilliseconds / FastDieMs;
                a = Math.Max(0, _fastDieFromAlpha * (1 - t));
                if (t >= 1) remove = true;
            }
            else if (c.Activated)
            {
                a = 255; // 已拼入中心字符串：完全实心、永久保留
            }
            else if (c == _target && _pressing)
            {
                a = 100 + 155 * ActivationProgress(now); // 激活中：100 → 255
            }
            else
            {
                a = LifeAlpha(c, now);
                if (PhaseOf(c, now) == RafflePhase.Expired) remove = true;
            }

            c.Alpha = a;
            c.Block.Opacity = Math.Clamp(a / 255.0, 0, 1);

            // 平滑移动到目标位置（激活归位）
            c.X += (c.TargetX - c.X) * 0.25;
            c.Y += (c.TargetY - c.Y) * 0.25;
            Canvas.SetLeft(c.Block, c.X - c.Width / 2);
            Canvas.SetTop(c.Block, c.Y - c.Height / 2);

            if (remove)
            {
                RaffleCanvas.Children.Remove(c.Block);
                _chars.RemoveAt(i);
            }
        }

        // 激活进度：满 1s 完全激活 → 位移中心 → 无缝衔接下一个
        if (_pressing)
        {
            if (_target is null || _target.Activated)
            {
                _target = PickRaffleTarget();
                _targetStartAt = now; // 选中即开始计时
            }

            if (_target is { Activated: false } target
                && ActivationProgress(now) >= ActivateSeconds)
            {
                target.Activated = true;
                _activated.Add(target);
                RelayoutActivated(cw, ch);
                _target = null; // 下帧无缝衔接激活下一个
            }
        }
    }

    /// <summary>生成一个新字符：随机位置（中心在卡片内，允许溢出显示不完全）+ 独立生命周期。</summary>
    private void SpawnRaffleChar(double cw, double ch)
    {
        string pick = RandomChar();
        Console.WriteLine($"[raffle] spawn: {pick}");
        var ft = new FormattedText(pick, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, RaffleTypeface, CharFontSize, RaffleBrush,
            VisualTreeHelper.GetDpi(RaffleCanvas).PixelsPerDip);

        var block = new TextBlock
        {
            Text = ft.Text,
            FontFamily = RaffleTypeface.FontFamily,
            FontSize = CharFontSize,
            FontWeight = FontWeights.Bold,
            Foreground = RaffleBrush
        };
        RaffleCanvas.Children.Add(block);

        double x = Random.Shared.NextDouble() * cw;
        double y = Random.Shared.NextDouble() * ch;
        var c = new RaffleChar
        {
            Text = ft.Text,
            Block = block,
            Width = ft.WidthIncludingTrailingWhitespace,
            Height = ft.Height,
            X = x,
            Y = y,
            TargetX = x,
            TargetY = y,
            BornAt = DateTime.Now,
            Alpha = 0
        };
        _chars.Add(c);
        Canvas.SetLeft(block, x - c.Width / 2);
        Canvas.SetTop(block, y - c.Height / 2);
        block.Opacity = 0;
    }

    /// <summary>生命周期 alpha（0-255）：出现 0→100，停留 100，消亡 100→0。</summary>
    private static double LifeAlpha(RaffleChar c, DateTime now)
    {
        double ms = (now - c.BornAt).TotalMilliseconds;
        if (ms < AppearMs) return 100.0 * ms / AppearMs;
        if (ms < HoldEndMs) return 100.0;
        if (ms < HoldEndMs + DieMs) return 100.0 * (HoldEndMs + DieMs - ms) / DieMs;
        return 0;
    }

    private static RafflePhase PhaseOf(RaffleChar c, DateTime now)
    {
        double ms = (now - c.BornAt).TotalMilliseconds;
        if (ms < AppearMs) return RafflePhase.Appear;
        if (ms < HoldEndMs) return RafflePhase.Hold;
        if (ms < HoldEndMs + DieMs) return RafflePhase.Die;
        return RafflePhase.Expired;
    }

    /// <summary>激活进度 0..1（按住时长 / 1s）。</summary>
    private double ActivationProgress(DateTime now)
        => Math.Clamp((now - _targetStartAt).TotalSeconds / ActivateSeconds, 0, 1);

    /// <summary>随机选一个「不在消亡周期」（出现 / 停留）且未激活的存活字符；无则 null。</summary>
    private RaffleChar? PickRaffleTarget()
    {
        var now = DateTime.Now;
        var candidates = new List<RaffleChar>();
        foreach (var c in _chars)
        {
            if (c.Activated) continue;
            if (c == _target) continue;
            if (PhaseOf(c, now) is RafflePhase.Appear or RafflePhase.Hold)
                candidates.Add(c);
        }
        return candidates.Count == 0 ? null
            : candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>把已激活字符串整体居中排列（新字符追加到末尾后重新计算所有目标位置）。</summary>
    private void RelayoutActivated(double cw, double ch)
    {
        if (cw <= 0 || ch <= 0 || _activated.Count == 0) return;

        double total = 0;
        foreach (var c in _activated) total += c.Width + ActivatedGap;
        double x = (cw - total) / 2;
        double cy = ch / 2;
        foreach (var c in _activated)
        {
            c.TargetX = x + (c.Width + ActivatedGap) / 2;
            c.TargetY = cy;
            x += c.Width + ActivatedGap;
        }
    }

    // ---------- 交互：长按激活 / 三击重置 ----------

    private void OnRaffleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // 三击：清空所有字符，banner「重置」平滑过渡
        if (e.ClickCount >= 3)
        {
            ResetRaffle();
            e.Handled = true;
            return;
        }

        _pressing = true;
        _target = PickRaffleTarget();
        _targetStartAt = DateTime.Now;
        RaffleCard.CaptureMouse();
    }

    private void OnRaffleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressing) return;
        _pressing = false;
        RaffleCard.ReleaseMouseCapture();

        var t = _target;
        _target = null;
        if (t is null || t.Activated) return;

        if (PhaseOf(t, DateTime.Now) == RafflePhase.Die)
        {
            // 松手时已在消亡周期：直接 0.05s 加速消亡
            t.DyingFast = true;
            t.FastDieStart = DateTime.Now;
            _fastDieFromAlpha = t.Alpha;
        }
        // 松手时在出现 / 停留周期：解除激活，恢复原生命周期继续走到消亡
    }

    /// <summary>清空随机字符并播放「重置」banner 过渡。</summary>
    private void ResetRaffle()
    {
        _pressing = false;
        _target = null;
        ClearRaffleChars();
        ShowRaffleResetBanner();
    }

    private void ClearRaffleChars()
    {
        foreach (var c in _chars) RaffleCanvas.Children.Remove(c.Block);
        _chars.Clear();
        _activated.Clear();
        _spawnAccum = 0;
    }

    /// <summary>显示「重置」banner：品牌蓝擦入覆盖气泡 → 停留 → 擦出（复用峰谷 / 用量共用 banner）。</summary>
    private void ShowRaffleResetBanner()
    {
        int gen = ++_bannerGeneration;

        BannerOverlay.Visibility = Visibility.Visible;
        BannerText.Text = "重置";
        BannerText.FontSize = 40;
        BannerText.Foreground = new SolidColorBrush(LightenTowardWhite(RaffleBrand, 0.35));
        var sway = new DoubleAnimation(-10, 10, TimeSpan.FromSeconds(1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        BannerTextTranslate.BeginAnimation(TranslateTransform.XProperty, sway);
        SetupBannerWipe(RaffleBrand, wipeIn: true);

        _ = HideRaffleResetBannerLater(gen);
    }

    private async System.Threading.Tasks.Task HideRaffleResetBannerLater(int gen)
    {
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(0.9));
        if (gen != _bannerGeneration) return;
        ClearWipeAnimations(BannerColor.Background);
        ClearWipeAnimations(BannerText.OpacityMask);
        SetupBannerWipe(RaffleBrand, wipeIn: false);
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(BannerWipeSeconds));
        if (gen != _bannerGeneration) return;
        HideBanner();
    }

    private static Brush CreateRaffleBrush()
    {
        var brush = new SolidColorBrush(RaffleBrand);
        brush.Freeze();
        return brush;
    }
}
