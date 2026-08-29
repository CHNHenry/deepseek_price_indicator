using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using PriceIndicator.Models;
using PriceIndicator.Services;

namespace PriceIndicator;

/// <summary>
/// 用量查询页面：DeepSeek 平台用量指标 + 两个空心饼图（今日 / 累计 token 组成）。
/// 悬停饼图段外径增大（内径不变）并高亮左侧对应行；长按卡片任意处手动刷新；
/// 每 N 分钟自动刷新；与手鼓猫互斥（见 <see cref="ToggleBongo"/>）。
/// </summary>
public partial class BubbleWindow
{
    // 用量查询（合并进气泡）状态
    private bool _usageActive;
    private DispatcherTimer? _usageAutoTimer;
    private bool _usageRefreshing;
    private UsageData? _usageData;

    /// <summary>下次自动刷新的时刻（用于“下次更新 -MM:SS”倒计时）。</summary>
    private DateTime _usageNextRefreshAt;

    /// <summary>下次更新倒计时的秒级刷新计时器。</summary>
    private readonly DispatcherTimer _usageCountdownTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    /// <summary>饼图段（Kind: 0=今日, 1=总；Index: 0=命中输入, 1=未命中, 2=输出）。</summary>
    private sealed class DonutSegment
    {
        public required Path Path;
        public required int Kind;
        public required int Index;
        public double Cx, Cy, InnerR, BaseOuterR, OuterR, StartAngle, Sweep;
        public bool Hovered;
    }

    private readonly List<DonutSegment> _segments = new();

    /// <summary>连接线终点模式：非 null 时连接线指向该饼图段几何中心（反向指示：悬停左侧行）。</summary>
    private DonutSegment? _lineToSegment;

    /// <summary>饼图段外径缓动动画计时器（悬停时外径增大、内径不变）。</summary>
    private readonly DispatcherTimer _segmentAnimTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };

    /// <summary>用量指标行矩阵 [Kind, Index] → (行容器, 下划线, 数值)。</summary>
    private readonly (FrameworkElement Row, Rectangle Underline, TextBlock Value)[,] _usageRows =
        new (FrameworkElement, Rectangle, TextBlock)[2, 3];

    /// <summary>当前长按进度环（用量卡片的 UsageHoldRing）。</summary>
    private Shape? _activeRing;
    private double _activeRingPerimeter;

    // 长按（Hold）状态：仅用于用量卡片手动刷新——
    // 左键按住时进度环沿卡片内缘增长（sin 缓动：先慢→中间快→后慢），
    // 满进度触发手动刷新；松开 / 移出则进度回退，进度连续。
    private readonly DispatcherTimer _holdTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16) // 约 60fps
    };
    private DateTime _holdStartTime;
    private double _holdElapsed; // 累计有效按住时长（毫秒），前进时增加、回退时减少
    private bool _isHolding;
    private bool _isRetreating;

    /// <summary>长按完成所需时长（毫秒），与 MyHoldButton 默认一致。</summary>
    private const double HoldTimeMs = 3000;

    /// <summary>用量强调色（与气泡边框同色）。</summary>
    private static readonly Color UsageAccent = Color.FromRgb(0x39, 0x64, 0xFE);

    /// <summary>用量饼图段悬停时外径增量（px），内径保持不变。</summary>
    private const double UsageSegmentExpand = 10;

    /// <summary>下划线厚度（px），连接细线同粗细。</summary>
    private const double UsageUnderlineThickness = 2;

    /// <summary>用量刷新 banner 最短总时长（秒），参考峰谷刷新 banner 时长（擦入 0.3s + 停留 + 擦出 0.3s）：
    /// 抓取太快时停留补足，避免 banner 一闪而过。</summary>
    private const double UsageBannerMinSeconds = 0.9;

    /// <summary>构造时调用：事件接线、计时器、面板引用等。</summary>
    private void WireUsageFeature()
    {
        // 用量面板尺寸变化触发宿主重排定位
        UsagePanel.SizeChanged += (_, _) => RequestHostLayout();

        // 用量卡片：长按手动刷新，进度环紧贴外框内侧
        UsageCard.SizeChanged += (_, _) => GenerateUsageHoldRing();
        UsageCard.MouseLeftButtonDown += OnUsageCardMouseDown;
        UsageCard.MouseLeftButtonUp += OnUsageCardMouseUp;
        UsageCard.MouseLeave += OnUsageCardMouseLeave;

        // 注意：OverlayCanvas（连接线覆盖层）不做任何显式 Width/Height 同步 ——
        // 它以默认 Stretch 对齐铺满 BubbleHost；若在此把它钉成宿主当前尺寸，
        // Canvas 的 DesiredSize 会反哺 Grid 测量，导致宿主只涨不缩（历史 bug：
        // 右下角偏离窗口中心 + banner 按开过的最大尺寸全覆盖）

        // 用量饼图段外径缓动（悬停突出显示）
        _segmentAnimTimer.Tick += (_, _) => TickUsageSegmentAnim();

        // 用量行矩阵 [Kind, Index] → (行容器, 下划线, 数值)；Index 与饼图段一致：
        // 0=命中输入 1=未命中输入 2=输出
        _usageRows[0, 0] = (RowUsageTodayInputHit, UlUsageTodayInputHit, ValUsageTodayInputHit);
        _usageRows[0, 1] = (RowUsageTodayInputMiss, UlUsageTodayInputMiss, ValUsageTodayInputMiss);
        _usageRows[0, 2] = (RowUsageTodayOutput, UlUsageTodayOutput, ValUsageTodayOutput);
        _usageRows[1, 0] = (RowUsageTotalInputHit, UlUsageTotalInputHit, ValUsageTotalInputHit);
        _usageRows[1, 1] = (RowUsageTotalInputMiss, UlUsageTotalInputMiss, ValUsageTotalInputMiss);
        _usageRows[1, 2] = (RowUsageTotalOutput, UlUsageTotalOutput, ValUsageTotalOutput);

        // 反向指示：悬停左侧行 → 下划线 + 连接线指向对应饼图段几何中心
        for (int k = 0; k < 2; k++)
            for (int i = 0; i < 3; i++)
            {
                var row = _usageRows[k, i].Row;
                row.SetValue(Panel.BackgroundProperty, Brushes.Transparent); // 整行可命中，悬停任意位置均触发
                row.Tag = (k, i);
                row.MouseEnter += OnUsageRowEnter;
                row.MouseLeave += OnUsageRowLeave;
            }

        // 长按（Hold）逻辑：用量卡片手动刷新
        _holdTimer.Tick += OnHoldTimerTick;

        // 下次更新倒计时：秒级刷新显示文本
        _usageCountdownTimer.Tick += (_, _) => TickUsageCountdown();
    }

    /// <summary>切换用量查询模式：开启时用量面板占据气泡，关闭时恢复普通气泡。</summary>
    public void ToggleUsage(bool enable)
    {
        if (enable && _bongoActive) ToggleBongo(false); // 与手鼓猫互斥
        if (enable && _audioActive) ToggleAudio(false); // 与音频监听互斥
        if (enable && _clockActive) ToggleClock(false); // 与桌面时钟互斥
        if (enable && _raffleActive) ToggleRaffle(false); // 与随机数选取器互斥
        if (enable == _usageActive) return;
        _usageActive = enable;
        if (Host is { } host) host.UsageMenuItem.IsChecked = enable;
        ++_bannerGeneration; // 作废进行中的 banner 动画

        if (enable)
        {
            // 进入用量模式：旧页面快速淡出、用量面板快速淡入（尺寸过渡见 TransitionToPage），暂停峰谷刷新
            _timer.Stop();
            BannerOverlay.Visibility = Visibility.Collapsed;
            TransitionToPage(UsagePanel);

            // 用量模式专用 banner 外观（覆盖大面板）：圆角与字号与 badge 模式不同
            BannerColor.CornerRadius = new CornerRadius(9);
            BannerText.FontSize = 26;

            LayoutUsage();
            if (_usageData is null)
                RefreshUsage(); // 首次进入抓取数据（banner 覆盖整面板）
            else
                UpdateUsageUi(_usageData);
            StartUsageAutoTimer();
        }
        else
        {
            // 恢复正常气泡
            _usageAutoTimer?.Stop();
            _usageCountdownTimer.Stop();
            HideUsageBanner();
            HideUsageRowHighlight();
            BannerColor.CornerRadius = new CornerRadius(7);
            BannerText.FontSize = 56;
            TransitionToPage(Badge); // 用量面板淡出、气泡淡入，右/下边缘钉住、左/上边缘滑回
            _timer.Start();
            Refresh();
            RequestHostLayout();
        }
    }

    /// <summary>测量用量面板并设置其高度（内容固定，模式切换 / 首次显示时计算一次）。</summary>
    private void LayoutUsage()
    {
        if (!_usageActive) return;
        UsageCard.Measure(new Size(UsagePanel.Width, double.PositiveInfinity));
        double target = Math.Ceiling(UsageCard.DesiredSize.Height);
        UsagePanel.Height = target;
        RequestHostLayout();
    }

    /// <summary>按配置间隔（默认 10 分钟）启动用量自动刷新计时器，并启动倒计时显示。</summary>
    private void StartUsageAutoTimer()
    {
        _usageAutoTimer?.Stop();
        int min = CurrentConfig.UsageRefreshIntervalMin;
        if (min <= 0) min = 10;
        _usageNextRefreshAt = DateTime.Now.AddMinutes(min);
        _usageAutoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(min) };
        _usageAutoTimer.Tick += (_, _) => { if (_usageActive) RefreshUsage(); };
        _usageAutoTimer.Start();
        _usageCountdownTimer.Start();
        TickUsageCountdown();
    }

    /// <summary>刷新“下次更新 -MM:SS”倒计时文本：总分钟数不折算成小时（60 分钟计 1 格，可超 60），秒每秒递减。</summary>
    private void TickUsageCountdown()
    {
        if (!_usageActive)
        {
            _usageCountdownTimer.Stop();
            return;
        }
        TimeSpan left = _usageNextRefreshAt - DateTime.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        NextUpdateText.Text = $"下次更新 -{(int)left.TotalMinutes:D2}:{left.Seconds:D2}";
    }

    /// <summary>手动 / 自动刷新用量：banner 覆盖气泡 → 抓取新数据 → 数据到达后擦除 banner。</summary>
    private async void RefreshUsage()
    {
        if (_usageRefreshing) return;
        _usageRefreshing = true;
        int gen = ++_bannerGeneration;

        var config = CurrentConfig;
        bool hasConfig = !string.IsNullOrWhiteSpace(config.DeepSeekMobile);
        if (!hasConfig && !DeepSeekUsageService.HasSession)
        {
            UsageStatusText.Text = "请在设置中填写账号";
            _usageRefreshing = false;
            return;
        }

        ShowUsageBanner(gen);
        var bannerShownAt = Stopwatch.StartNew();

        try
        {
            var data = await DeepSeekUsageService.FetchAsync(
                config.DeepSeekMobile, config.DeepSeekPassword);
            _usageData = data;
            UpdateUsageUi(data);
            UsageStatusText.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
            HideUsageError();
            StartUsageAutoTimer(); // 间隔可能在设置中被改动，按最新配置重启
        }
        catch (Exception ex)
        {
            ShowUsageError(ex.Message);
        }
        finally
        {
            // banner 总时长至少 UsageBannerMinSeconds：抓取太快时补足停留，再开始擦出
            double remain = UsageBannerMinSeconds - bannerShownAt.Elapsed.TotalSeconds;
            if (remain > 0)
                await Task.Delay(TimeSpan.FromSeconds(remain));
            HideUsageBanner(gen);
            _usageRefreshing = false;
        }
    }

    /// <summary>显示刷新失败提示：固定标签「刷新失败」+ 原因文字在约 8 个半角字符宽的
    /// 滚动条（UsageErrorScroll）内从右向左循环滚动。同时清空「更新于…」状态文字。</summary>
    private void ShowUsageError(string reason)
    {
        UsageStatusText.Text = "";
        UsageErrorText.Text = reason;
        UsageErrorTranslate.BeginAnimation(TranslateTransform.XProperty, null); // 清掉上一轮滚动动画
        UsageErrorArea.Visibility = Visibility.Visible;

        double scrollW = UsageErrorScroll.Width;
        // 文字位于 Canvas 内，按自身自然宽度测量渲染（不受滚动区 39.25 宽约束），
        // 因此 ActualWidth 就是文字自然宽，全文完整参与滚动
        UsageErrorArea.UpdateLayout(); // 同步布局，确保 ActualWidth 已按自然宽更新
        double textW = UsageErrorText.ActualWidth;
        if (textW <= 0) textW = reason.Length * 9.5; // 布局未完成时的近似宽（全角字 ≈ 字号）
        // 原因从滚动区右缘进入，完全移出左缘后循环；速度约 30px/s
        var scroll = new DoubleAnimation(scrollW, -textW, TimeSpan.FromSeconds((scrollW + textW) / 30))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        UsageErrorTranslate.BeginAnimation(TranslateTransform.XProperty, scroll);
    }

    /// <summary>隐藏刷新失败提示并停止滚动动画。</summary>
    private void HideUsageError()
    {
        UsageErrorArea.Visibility = Visibility.Collapsed;
        UsageErrorTranslate.BeginAnimation(TranslateTransform.XProperty, null);
    }

    /// <summary>用新数据更新全部指标与两个空心饼图（总 = 本地累计，由服务端提供）。</summary>
    private void UpdateUsageUi(UsageData d)
    {
        ValUsageRemaining.Text = FormatUsageMoney(d.RemainingBalance);
        ValUsageTotalSpent.Text = FormatUsageMoney(d.TotalSpent);
        ValUsageTodayCost.Text = FormatUsageMoney(d.Today.Cost);
        ValUsageTodayInputHit.Text = FormatUsageToken(d.Today.InputHitToken);
        ValUsageTodayInputMiss.Text = FormatUsageToken(d.Today.InputMissToken);
        ValUsageTodayOutput.Text = FormatUsageToken(d.Today.OutputToken);
        ValUsageTodayHitRate.Text = FormatUsageRate(d.Today.InputHitToken, d.Today.InputMissToken);
        ValUsageTotalInputHit.Text = FormatUsageToken(d.Total.InputHitToken);
        ValUsageTotalInputMiss.Text = FormatUsageToken(d.Total.InputMissToken);
        ValUsageTotalOutput.Text = FormatUsageToken(d.Total.OutputToken);
        ValUsageTotalHitRate.Text = FormatUsageRate(d.Total.InputHitToken, d.Total.InputMissToken);

        // 饼图段顺序：命中输入 / 未命中 / 输出
        BuildUsageDonut(UsageTodayCanvas, new[] { d.Today.InputHitToken, d.Today.InputMissToken, d.Today.OutputToken }, 0);
        BuildUsageDonut(UsageTotalCanvas, new[] { d.Total.InputHitToken, d.Total.InputMissToken, d.Total.OutputToken }, 1);
    }

    private static string FormatUsageMoney(double v) => "¥" + v.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>token 数：千分位完整显示（不缩写）。123456789 to 123,456,789</summary>
    private static string FormatUsageToken(long v)
        => v.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatUsageRate(long hit, long miss)
    {
        long total = hit + miss;
        return total <= 0 ? "N/A"
            : (hit / (double)total * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>在指定画布上构建 3 段空心饼图（命中输入 / 未命中 / 输出）。</summary>
    private void BuildUsageDonut(Canvas canvas, long[] values, int kind)
    {
        _segments.RemoveAll(s => s.Kind == kind);
        _segmentAnimTimer.Stop();
        canvas.Children.Clear();
        HideUsageRowHighlight(); // 重建时清掉残留的悬停高亮

        long total = values.Sum();
        if (total <= 0) return;

        double cx = canvas.Width / 2;
        double cy = canvas.Height / 2;
        const double outerR = 28, innerR = 17;
        Color[] colors =
        {
            Color.FromRgb(0xA0, 0xDC, 0xFD), // 命中输入：浅蓝
            Color.FromRgb(0x60, 0xB3, 0xFE), // 未命中：中蓝
            Color.FromRgb(0x0C, 0x70, 0xF3)  // 输出：深蓝
        };

        double start = -90; // 从顶部 12 点方向顺时针
        for (int i = 0; i < values.Length; i++)
        {
            double sweep = (double)values[i] / total * 360;
            start += sweep;
            if (sweep < 0.001) continue; // 数值为 0 的段不绘制

            var seg = new DonutSegment
            {
                Path = new Path { Fill = new SolidColorBrush(colors[i]) },
                Kind = kind,
                Index = i,
                Cx = cx,
                Cy = cy,
                InnerR = innerR,
                BaseOuterR = outerR,
                OuterR = outerR,
                StartAngle = start - sweep,
                Sweep = sweep
            };
            seg.Path.Data = CreateUsageSector(cx, cy, outerR, innerR, seg.StartAngle, seg.Sweep);
            seg.Path.MouseEnter += OnUsageSegmentEnter;
            seg.Path.MouseMove += OnUsageSegmentMove;
            seg.Path.MouseLeave += OnUsageSegmentLeave;
            canvas.Children.Add(seg.Path);
            _segments.Add(seg);
        }
    }

    /// <summary>构造一段环形扇区（外弧 + 内弧闭合），角度单位为度，0° 指向 3 点方向。</summary>
    private static Geometry CreateUsageSector(double cx, double cy, double outerR, double innerR,
        double startDeg, double sweepDeg)
    {
        double a1 = startDeg * Math.PI / 180;
        double a2 = (startDeg + sweepDeg) * Math.PI / 180;
        bool large = sweepDeg > 180;

        var p1 = new Point(cx + innerR * Math.Cos(a1), cy + innerR * Math.Sin(a1));
        var p2 = new Point(cx + outerR * Math.Cos(a1), cy + outerR * Math.Sin(a1));
        var p3 = new Point(cx + outerR * Math.Cos(a2), cy + outerR * Math.Sin(a2));
        var p4 = new Point(cx + innerR * Math.Cos(a2), cy + innerR * Math.Sin(a2));

        var fig = new PathFigure { StartPoint = p1, IsClosed = false };
        fig.Segments.Add(new LineSegment(p2, true));
        fig.Segments.Add(new ArcSegment(p3, new Size(outerR, outerR), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(p4, true));
        fig.Segments.Add(new ArcSegment(p1, new Size(innerR, innerR), 0, large, SweepDirection.Counterclockwise, true));

        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        return geom;
    }

    // ---------- 饼图悬停：外径增大（内径不变）+ 下划线 + 连接细线 ----------

    private void OnUsageSegmentEnter(object sender, MouseEventArgs e)
    {
        if (FindUsageSegment(sender as Path) is not { } seg) return;
        seg.Hovered = true;
        seg.Path.Cursor = Cursors.Hand;
        _lineToSegment = null; // 直接悬停饼图段：连接线指向鼠标
        ShowUsageRowHighlight(seg);
        UpdateUsageConnectLine(seg, e.GetPosition(OverlayCanvas));
        EnsureUsageSegmentAnim();
    }

    private void OnUsageSegmentMove(object sender, MouseEventArgs e)
    {
        if (FindUsageSegment(sender as Path) is not { } seg) return;
        UpdateUsageConnectLine(seg, e.GetPosition(OverlayCanvas));
    }

    private void OnUsageSegmentLeave(object sender, MouseEventArgs e)
    {
        if (FindUsageSegment(sender as Path) is not { } seg) return;
        seg.Hovered = false;
        _lineToSegment = null;
        HideUsageRowHighlight();
        EnsureUsageSegmentAnim();
    }

    private DonutSegment? FindUsageSegment(Path? p)
    {
        foreach (var s in _segments)
            if (s.Path == p) return s;
        return null;
    }

    // ---------- 反向指示：悬停左侧行 → 下划线 + 连接线指向对应饼图段几何中心 ----------

    private void OnUsageRowEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not (int kind, int index)) return;
        if (FindUsageSegmentByKey(kind, index) is not { } seg) return;

        seg.Hovered = true;                 // 让对应饼图段外径放大（与正向一致）
        _lineToSegment = seg;               // 连接线指向该段几何中心
        ShowUsageRowHighlight(seg);
        UpdateUsageSegmentCenterLine(seg);
        EnsureUsageSegmentAnim();
    }

    private void OnUsageRowLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not (int kind, int index)) return;
        if (FindUsageSegmentByKey(kind, index) is not { } seg) return;

        seg.Hovered = false;
        _lineToSegment = null;
        HideUsageRowHighlight();
        EnsureUsageSegmentAnim();
    }

    /// <summary>按 (Kind, Index) 查找饼图段；不存在（该值为 0 未绘制）时返回 null。</summary>
    private DonutSegment? FindUsageSegmentByKey(int kind, int index)
    {
        foreach (var s in _segments)
            if (s.Kind == kind && s.Index == index) return s;
        return null;
    }

    /// <summary>环形扇区（饼图段）的几何中心（质心）：位于角平分线上，
    /// 到圆心距离 r̄ = 2/3·(R³−r³)/(R²−r²)；外径 R 随悬停放大而变化，故中心随之外移。</summary>
    private static Point UsageSegmentCenter(DonutSegment s)
    {
        double mid = (s.StartAngle + s.Sweep / 2) * Math.PI / 180;
        double r = 2.0 / 3.0
            * (s.OuterR * s.OuterR * s.OuterR - s.InnerR * s.InnerR * s.InnerR)
            / (s.OuterR * s.OuterR - s.InnerR * s.InnerR);
        return new Point(s.Cx + r * Math.Cos(mid), s.Cy + r * Math.Sin(mid));
    }

    /// <summary>连接线：起点 = 左侧行下划线末端，终点 = 对应饼图段几何中心（随外径缩放实时变化）。</summary>
    private void UpdateUsageSegmentCenterLine(DonutSegment seg)
    {
        if (_lineToSegment != seg) return;
        var (row, _, _) = _usageRows[seg.Kind, seg.Index];
        var p = row.TranslatePoint(new Point(row.ActualWidth, row.ActualHeight), OverlayCanvas);
        Point center = seg.Path.TranslatePoint(UsageSegmentCenter(seg), OverlayCanvas);
        ConnectLine.X1 = p.X;
        ConnectLine.Y1 = p.Y - UsageUnderlineThickness / 2;
        ConnectLine.X2 = center.X;
        ConnectLine.Y2 = center.Y;
        ConnectLine.Opacity = 1;
    }

    /// <summary>高亮左侧对应行：显示下划线 + 数值变强调色。</summary>
    private void ShowUsageRowHighlight(DonutSegment seg)
    {
        var (_, underline, value) = _usageRows[seg.Kind, seg.Index];
        underline.Opacity = 1;
        value.Foreground = new SolidColorBrush(UsageAccent);
    }

    /// <summary>清除全部行的下划线高亮并隐藏连接线。</summary>
    private void HideUsageRowHighlight()
    {
        for (int k = 0; k < 2; k++)
            for (int i = 0; i < 3; i++)
            {
                _usageRows[k, i].Underline.Opacity = 0;
                _usageRows[k, i].Value.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
            }
        ConnectLine.Opacity = 0;
    }

    /// <summary>连接线：起点 = 左侧对应行下划线末端，终点 = 鼠标位置。</summary>
    private void UpdateUsageConnectLine(DonutSegment seg, Point mouse)
    {
        var (row, _, _) = _usageRows[seg.Kind, seg.Index];
        var p = row.TranslatePoint(new Point(row.ActualWidth, row.ActualHeight), OverlayCanvas);
        ConnectLine.X1 = p.X;
        ConnectLine.Y1 = p.Y - UsageUnderlineThickness / 2;
        ConnectLine.X2 = mouse.X;
        ConnectLine.Y2 = mouse.Y;
        ConnectLine.Opacity = 1;
    }

    /// <summary>外径缓动：每帧向目标值靠近 35%（悬停 +SegmentExpand，移出回 Base），内径保持不变。</summary>
    private void EnsureUsageSegmentAnim()
    {
        bool any = false;
        foreach (var s in _segments)
        {
            double target = s.Hovered ? s.BaseOuterR + UsageSegmentExpand : s.BaseOuterR;
            if (Math.Abs(s.OuterR - target) > 0.1) any = true;
        }
        if (any) _segmentAnimTimer.Start(); else _segmentAnimTimer.Stop();
    }

    private void TickUsageSegmentAnim()
    {
        bool any = false;
        foreach (var s in _segments)
        {
            double target = s.Hovered ? s.BaseOuterR + UsageSegmentExpand : s.BaseOuterR;
            double next = s.OuterR + (target - s.OuterR) * 0.35;
            if (Math.Abs(next - s.OuterR) < 0.02) next = target;
            if (Math.Abs(s.OuterR - target) > 0.1)
            {
                s.OuterR = next;
                s.Path.Data = CreateUsageSector(s.Cx, s.Cy, s.OuterR, s.InnerR, s.StartAngle, s.Sweep);
                if (_lineToSegment == s) UpdateUsageSegmentCenterLine(s); // 几何中心随外径缩放移动
                any = true;
            }
        }
        if (!any) _segmentAnimTimer.Stop();
    }

    // ---------- 用量刷新 banner（边框色擦入 / 擦出，文字“刷新中…”） ----------

    /// <summary>显示用量刷新 banner：边框色从左向右擦入覆盖整个用量气泡。</summary>
    private void ShowUsageBanner(int gen)
    {
        BannerOverlay.Visibility = Visibility.Visible;
        BannerText.Text = "刷新中…";
        BannerText.Foreground = new SolidColorBrush(LightenTowardWhite(UsageAccent, 0.35));
        var sway = new DoubleAnimation(-10, 10, TimeSpan.FromSeconds(1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        BannerTextTranslate.BeginAnimation(TranslateTransform.XProperty, sway);
        SetupBannerWipe(UsageAccent, wipeIn: true);
    }

    /// <summary>擦除用量刷新 banner：从“完全覆盖”状态收回后隐藏。</summary>
    private async void HideUsageBanner(int gen = -1)
    {
        if (gen != -1 && gen != _bannerGeneration) return;
        // 从完全覆盖状态开始擦出（若擦入未完成则直接跳到全覆盖，避免两条动画互相干扰）
        ClearWipeAnimations(BannerColor.Background);
        ClearWipeAnimations(BannerText.OpacityMask);
        SetupBannerWipe(UsageAccent, wipeIn: false);
        await Task.Delay(TimeSpan.FromSeconds(BannerWipeSeconds));
        if (gen != -1 && gen != _bannerGeneration) return;
        HideBanner();
    }

    // ---------- 用量卡片长按刷新 ----------

    /// <summary>长按进度环圆角半径（px），可微调以贴合边框内缘圆角。</summary>
    private const double UsageHoldRingRadius = 10;

    /// <summary>进度环淡入区间：进度 0 → 该值时透明度 0 → 1 线性跟随。
    /// 快速点按（进度极小）时环近乎全透明，不随点击闪现弧段。</summary>
    private const double HoldRingFadeProgress = 0.05;

    /// <summary>
    /// 生成沿卡片内缘的进度环：Path 沿圆角矩形轮廓走一圈，路径起点 = 顶部 12 点。
    /// 进度用 StrokeDashArray 控制（不设 StrokeDashOffset）：实线从路径起点（顶部中点）起顺时针增长，
    /// 实线长度 = 周长 × 进度，起点固定不漂移。
    /// 环矩形必须以卡片实际尺寸为基准计算，并显式钉死 Path 的 Width/Height——
    /// 绝不能读环自身的 ActualWidth/ActualHeight：几何会反过来撑大布局，
    /// 触发 SizeChanged → 再生成 → 再撑大（每次约 +半个线宽），环最终被画得比卡片还大，
    /// 右/下边缘整条溢出面板被裁掉（Margin 怎么调都救不回来）。
    /// </summary>
    private void GenerateUsageHoldRing()
    {
        // 目标矩形 = 卡片内缘 − Path.Margin（几何在 Path 自身坐标系里，Margin 只决定摆放位置）
        double w = UsageCard.ActualWidth - UsageCard.BorderThickness.Left - UsageCard.BorderThickness.Right
                   - UsageHoldRing.Margin.Left - UsageHoldRing.Margin.Right;
        double h = UsageCard.ActualHeight - UsageCard.BorderThickness.Top - UsageCard.BorderThickness.Bottom
                   - UsageHoldRing.Margin.Top - UsageHoldRing.Margin.Bottom;
        if (w <= 0 || h <= 0) return;

        // 钉死布局尺寸：环不再“量自己”，期望尺寸恒定，布局收敛不再膨胀
        UsageHoldRing.Width = w;
        UsageHoldRing.Height = h;

        double r = UsageHoldRingRadius;

        // 从顶部中点起，顺时针绕一圈圆角矩形轮廓（四段直线 + 四段 90° 圆弧），最后回到起点闭合
        var fig = new PathFigure { StartPoint = new Point(w / 2, 0), IsClosed = false };
        fig.Segments.Add(new LineSegment(new Point(w - r, 0), true));                  // 顶边
        fig.Segments.Add(new ArcSegment(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));     // 右上角
        fig.Segments.Add(new LineSegment(new Point(w, h - r), true));                  // 右边
        fig.Segments.Add(new ArcSegment(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true)); // 右下角
        fig.Segments.Add(new LineSegment(new Point(r, h), true));                      // 底边
        fig.Segments.Add(new ArcSegment(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true)); // 左下角
        fig.Segments.Add(new LineSegment(new Point(0, r), true));                      // 左边
        fig.Segments.Add(new ArcSegment(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true));     // 左上角
        fig.Segments.Add(new LineSegment(new Point(w / 2, 0), true));                  // 回到顶部中点（闭合）

        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        UsageHoldRing.Data = geom;

        _activeRing = UsageHoldRing;
        // 圆角矩形周长：四段直线 + 四段 90° 圆弧
        _activeRingPerimeter = 2 * (w - 2 * r) + 2 * (h - 2 * r) + 2 * Math.PI * r;
        double units = _activeRingPerimeter / UsageHoldRing.StrokeThickness;
        UsageHoldRing.StrokeDashArray = new DoubleCollection { 0, units };
        UsageHoldRing.Opacity = 0;
    }

    private void OnUsageCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        GenerateUsageHoldRing(); // 确保周长与几何与当前尺寸一致
        StartHold();
        e.Handled = true; // 防止冒泡
    }

    private void OnUsageCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        StartRetreat();
        e.Handled = true;
    }

    private void OnUsageCardMouseLeave(object sender, MouseEventArgs e)
    {
        StartRetreat();
    }

    // ---------- 长按（Hold）逻辑：用量卡片手动刷新 ----------

    /// <summary>根据进度（0~1）更新当前活动进度环：实线长度 = 周长 × 进度；
    /// 透明度与进度同步淡入（0 → HoldRingFadeProgress 区间线性 0 → 1），
    /// 前进 / 回退经过同一进度值时弧长与透明度完全一致，无跳变、无点按闪烁。</summary>
    private void UpdateHoldRing(double progress)
    {
        if (_activeRing is null) return;
        double thick = _activeRing.StrokeThickness;
        _activeRing.StrokeDashArray = new DoubleCollection
        {
            _activeRingPerimeter * progress / thick,
            _activeRingPerimeter / thick
        };
        _activeRing.Opacity = Math.Min(1, progress / HoldRingFadeProgress);
    }

    /// <summary>重置当前活动进度环：隐藏并清零。</summary>
    private void ResetHoldRing()
    {
        if (_activeRing is null) return;
        _activeRing.Opacity = 0;
        double dashUnits = _activeRingPerimeter > 0 ? _activeRingPerimeter / _activeRing.StrokeThickness : 0;
        _activeRing.StrokeDashArray = new DoubleCollection { 0, dashUnits };
    }

    /// <summary>长按计时器：前进 / 回退共用，每次触发按经过的时间更新进度环。</summary>
    private void OnHoldTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        double dt = (now - _holdStartTime).TotalMilliseconds;
        _holdStartTime = now;

        if (_isHolding)
        {
            // 前进：累计按住时长
            _holdElapsed += dt;

            if (_holdElapsed >= HoldTimeMs)
            {
                // 长按完成：重置并触发手动刷新
                _holdElapsed = 0;
                _isHolding = false;
                _holdTimer.Stop();
                ResetHoldRing();
                OnHoldCompleted();
                return;
            }

            UpdateHoldRing(HoldProgress(_holdElapsed));
        }
        else if (_isRetreating)
        {
            // 回退：与前进同速扣减
            _holdElapsed -= dt;

            if (_holdElapsed <= 0)
            {
                _holdElapsed = 0;
                _isRetreating = false;
                _holdTimer.Stop();
                ResetHoldRing();
                return;
            }

            // 必须与前进使用完全相同的 sin 缓动公式换算显示进度，
            // 否则松开瞬间进度会从 sin(t) 突跳为线性 t，造成进度环跳动
            UpdateHoldRing(HoldProgress(_holdElapsed));
        }
    }

    /// <summary>sin 缓动进度：先慢→中间快→后慢。</summary>
    private static double HoldProgress(double elapsed)
    {
        double raw = elapsed / HoldTimeMs;
        return (1 - Math.Cos(raw * Math.PI)) / 2;
    }

    /// <summary>开始长按前进；若之前正在回退，则从当前进度继续。</summary>
    private void StartHold()
    {
        if (!IsEnabled) return;
        _holdStartTime = DateTime.Now;
        _isHolding = true;
        _isRetreating = false;
        // 立即按当前进度恢复环（弧长 + 淡入透明度），不直接置 Opacity=1：
        // 快速点按的进度极小 → 透明度趋近 0，点按不再闪现弧段；
        // 从回退中重新按住时也能无跳变地接上当前进度
        UpdateHoldRing(HoldProgress(_holdElapsed));
        _holdTimer.Start();
    }

    /// <summary>开始回退进度；无进度可回退时停表并隐藏进度环。</summary>
    private void StartRetreat()
    {
        _isHolding = false;
        if (_holdElapsed <= 0)
        {
            _isRetreating = false;
            _holdTimer.Stop();
            ResetHoldRing();
            return;
        }
        _isRetreating = true;
        _holdStartTime = DateTime.Now;
        _holdTimer.Start();
    }

    /// <summary>长按完成：若按住的是用量卡片，则触发手动刷新。</summary>
    private void OnHoldCompleted()
    {
        if (ReferenceEquals(_activeRing, UsageHoldRing))
            RefreshUsage();
    }
}
