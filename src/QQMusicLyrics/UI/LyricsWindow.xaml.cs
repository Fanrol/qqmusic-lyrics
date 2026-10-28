using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Interop;
using QQMusicLyrics.Media;
using QQMusicLyrics.Models;
using QQMusicLyrics.Settings;

namespace QQMusicLyrics.UI;

/// <summary>
/// 浮窗本体：无边框 + 完全透明 + 置顶 + 不出现在 Alt+Tab/任务栏。
///
/// 歌词是**恒定字号**、单行、不换行、不省略：一行太长时窗口会向左右两侧撑开
/// （文字始终以原中心居中），所以长句会"凸出"480 的基础框，而不是被压缩或换行。
///
/// 锁定语义：锁定 = 鼠标穿透，但**锁图标那一小块会临时取消穿透**，
/// 所以锁定状态下也能点图标解锁（与 QQ 音乐自带歌词的行为一致）。
/// 悬停判定不依赖窗口事件（穿透时收不到），而是轮询光标位置。
/// </summary>
public partial class LyricsWindow : Window
{
    /// <summary>歌词左右留白（与 XAML 里 ContentPanel 的 Margin 一致）。</summary>
    private const double LineSidePadding = 16;

    /// <summary>字号下限，再小就没法读了。</summary>
    private const double MinFontSize = 12;

    /// <summary>字号上限（与托盘/滚轮一致）。</summary>
    private const double MaxFontSize = 120;

    /// <summary>鼠标离开浮窗多久后隐藏按钮栏。</summary>
    private static readonly TimeSpan ButtonBarHideDelay = TimeSpan.FromSeconds(1);

    // 三个按钮的布局：锁定 · 加号 · 减号，整组居中
    private const double ButtonSize = 24;
    private const double GapLockToPlus = 14;
    private const double GapPlusToMinus = 6;

    /// <summary>整组按钮的宽度 = 24*3 + 14 + 6 = 92。</summary>
    private const double ButtonGroupWidth = ButtonSize * 3 + GapLockToPlus + GapPlusToMinus;

    /// <summary>进度条宽度（居中）。比框的 480 窄一些，看着更像一条进度而不是分隔线。</summary>
    private const double ProgressBarWidth = 240;

    /// <summary>锁图标点击区的判定范围（比图标本身大一圈，避免"移过去的一瞬间还没取消穿透"）。</summary>
    private const double LockZoneHalfWidth = 20;
    private const double LockZoneBottom = 38;

    private readonly SmtcMediaService _media;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _ticker;

    private ResolvedLyrics _lyrics = ResolvedLyrics.Empty;
    private int _lineIndex = -1;
    private bool _showingLyrics;

    /// <summary>Loaded 之后才允许改动可见性：在窗口真正 Show 之前把它设成 Hidden 会留下不一致状态。</summary>
    private bool _ready;

    private IntPtr _handle = IntPtr.Zero;

    private bool _dragging;
    private Point _dragStartMouse;   // 按下时鼠标的屏幕位置（DIP）
    private double _dragOriginLeft;
    private double _dragOriginTop;

    // 按钮栏的悬停状态
    private bool _cursorInside;
    private bool _buttonBarShown;
    private DateTime _cursorLeftAt = DateTime.UtcNow;

    /// <summary>光标是否落在锁图标的点击区内（只有这块区域在锁定时不穿透）。</summary>
    private bool _cursorOverLockZone;

    /// <summary>"框中心"的物理像素位置（整数）。窗口宽度变化时用它反算 Left，保证按钮一格都不动。</summary>
    private int _centerAnchorPhysical;
    private bool _centerAnchorReady;

    /// <summary>ticker 计数：用来周期性重申置顶。</summary>
    private int _tickCount;

    /// <summary>上一次进度条填充到的整像素位置，变化不大就不重设布局。</summary>
    private int _lastProgressPixels = -1;

    /// <summary>请求弹出右键菜单（由托盘统一提供菜单，避免两套菜单不一致）。</summary>
    public event EventHandler? ContextMenuRequested;

    /// <summary>用户点了浮窗上的锁图标。</summary>
    public event EventHandler? LockToggleRequested;

    public bool IsUserHidden { get; private set; }

    public LyricsWindow(SmtcMediaService media, AppSettings settings)
    {
        _media = media;
        _settings = settings;

        InitializeComponent();

        // 注意顺序：先把尺寸落到窗口上，RestorePosition 里算"首次运行默认位置"时才有正确的宽高可用
        Width = settings.Width;
        Height = settings.Height;
        Opacity = settings.WindowOpacity;

        RestorePosition();
        ApplySettings();

        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33), // ~30fps，用于换行判断与按钮栏悬停检测
        };
        _ticker.Tick += OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            _ready = true;
            ApplySettings();
            CaptureCenterAnchor(); // 位置定下来之后再记锚点
            UpdateVisibility();
            _ticker.Start();
        };
        Closing += (_, _) => SaveBounds();

        // QQ 音乐会话出现/消失时立刻响应，不依赖 30fps 的轮询
        _media.SessionAvailabilityChanged += OnSessionAvailabilityChanged;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += OnMouseRightButtonUp;
        MouseWheel += OnMouseWheel;

        LockButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; LockToggleRequested?.Invoke(this, EventArgs.Empty); };
        PlusButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; AdjustFontSize(+2); };
        MinusButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; AdjustFontSize(-2); };

        // 进度条交界处光点的"呼吸"：很轻的明暗脉动，做出焊接似的感觉
        ProgressGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        SetStatus(string.Empty);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyOverlayStyles(_handle);
        UpdateClickThrough();
    }

    // ---------------- 外观 ----------------

    public void ApplySettings()
    {
        Height = _settings.Height;
        Opacity = _settings.WindowOpacity;
        Topmost = true;
        NativeMethods.RaiseToTopMost(_handle);

        StatusLine.FontSize = _settings.FontSize;

        // "框"的宽度永远取设置里的基础宽度（默认 480），不随歌词变宽而改变；
        // 以后加进来的内容都挂在这个框里，坐标系是稳定的。
        BoxContainer.Width = _settings.Width;
        ProgressHost.Width = ProgressBarWidth;
        ProgressTrack.Width = ProgressBarWidth;
        _lastProgressPixels = -1; // 宽度变了，进度条要重算

        UpdateLockAppearance();
        UpdateClickThrough();

        // 让字号、翻译开关这类改动立刻反映到画面上，并重新计算窗口宽度
        if (_showingLyrics && _lineIndex >= 0)
        {
            RenderCurrentLine(animate: false);
        }
        else if (StatusLine.Visibility == Visibility.Visible)
        {
            LayoutWindowWidth(StatusLine.Text, StatusLine.FontSize);
        }
        else
        {
            LayoutWindowWidth(null, _settings.FontSize);
        }

        UpdateVisibility();
    }

    /// <summary>根据锁定状态切换锁图标形态；加减号只在未锁定（A 状态）出现。</summary>
    private void UpdateLockAppearance()
    {
        bool locked = _settings.Locked;

        LockShackleClosed.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        LockShackleOpen.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        // 用 Hidden 而不是 Collapsed：Hidden 仍占位，锁图标的位置就不会随状态左右跳
        Visibility editVisibility = locked ? Visibility.Hidden : Visibility.Visible;
        PlusButton.Visibility = editVisibility;
        MinusButton.Visibility = editVisibility;
    }

    /// <summary>
    /// 鼠标穿透只作用于"除锁图标以外"的区域。
    ///
    /// WS_EX_TRANSPARENT 是整窗级别的，没法只对一部分生效，所以这里按光标位置动态切换：
    ///   - 未锁定：整窗可交互（能拖动、能右键、能点加减号）
    ///   - 锁定 + 光标不在锁图标上：整窗穿透
    ///   - 锁定 + 光标移到锁图标的点击区：临时取消穿透，让点击能落到图标上（点它可以解锁）
    /// 判定用轮询而不是鼠标事件，因为穿透时窗口根本收不到事件。
    /// </summary>
    private void UpdateClickThrough()
    {
        if (_handle == IntPtr.Zero) return; // 窗口还没创建

        bool clickThrough = _settings.Locked && !_cursorOverLockZone;
        NativeMethods.SetClickThrough(_handle, clickThrough);
    }

    /// <summary>锁图标在窗口内的水平中心：整组按钮居中后，锁在最左边。</summary>
    private double LockCenterX => Width / 2.0 - ButtonGroupWidth / 2.0 + ButtonSize / 2.0;

    private bool IsInLockZone(double localX, double localY)
    {
        return Math.Abs(localX - LockCenterX) <= LockZoneHalfWidth &&
               localY >= 0 && localY <= LockZoneBottom;
    }

    private void RestorePosition()
    {
        // 这里必须全程使用 DIP（设备无关像素）。
        // 坑：WinForms 的 Screen.WorkingArea 返回物理像素，而 Window.Left/Top/Width/Height 都是 DIP。
        // 在 150% 缩放的屏幕上混用会把窗口算到屏幕外面（开发时实测：窗口跑到了 y=1842，屏幕只有 1600 高）。
        // SystemParameters.WorkArea 正好是 DIP 单位，直接用它就没有换算问题。
        Rect work = SystemParameters.WorkArea;

        if (_settings.Left is double savedLeft && _settings.Top is double savedTop &&
            IsOnAnyScreen(savedLeft, savedTop))
        {
            Left = savedLeft;
            Top = savedTop;
        }
        else
        {
            // 首次运行：屏幕底部居中，离任务栏留一点距离
            Left = work.Left + (work.Width - Width) / 2.0;
            Top = work.Bottom - Height - 110;
        }
    }

    /// <summary>判断保存的位置是否还落在可见区域内（拔掉显示器后不能把窗口丢到屏幕外）。同样用 DIP 比较。</summary>
    private static bool IsOnAnyScreen(double left, double top)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        return left >= vLeft - 200 && left <= vRight - 100 &&
               top >= vTop - 100 && top <= vBottom - 100;
    }

    private void SaveBounds()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        // 窗口宽度会被长歌词撑大（Width 不再等于设置里的基础宽度），
        // 所以这里存"基础框"的左边界，重启后长句仍围绕同一个中心展开。
        double extra = Width - _settings.Width;
        _settings.Left = Left + extra / 2.0;
        _settings.Top = Top;
        _settings.Save();
    }

    // ---------------- 显示 / 隐藏 ----------------

    /// <summary>
    /// 是否显示浮窗。只有"用户手动隐藏"或"QQ 音乐没有媒体会话"时才隐藏。
    /// 暂停播放不会隐藏——暂停时会话仍然存在，歌词停在当前位置。
    ///
    /// 这里刻意对比窗口真实的 <see cref="UIElement.Visibility"/> 而不是自己维护一个布尔缓存：
    /// 缓存标志一旦和真实状态失步（例如窗口尚未 Show 就被设成 Hidden），
    /// 后面算出来"应该显示"时会被当成"状态没变"直接跳过，窗口就永远不出现了。
    /// </summary>
    private void UpdateVisibility()
    {
        if (!_ready) return; // 窗口还没 Show 过，先不要动

        bool shouldHide = IsUserHidden || (_settings.HideWhenNoQqMusic && !_media.HasSession);
        Visibility target = shouldHide ? Visibility.Hidden : Visibility.Visible;

        if (Visibility != target)
        {
            Visibility = target;
            Log.Info($"浮窗{(shouldHide ? "隐藏" : "显示")}（QQ 音乐会话：{(_media.HasSession ? "有" : "无")}）");

            if (!shouldHide)
            {
                _lineIndex = -1;       // 重新显示时强制重画
                _cursorInside = false; // 悬停状态重新判断
                _cursorOverLockZone = false;
                UpdateClickThrough();
            }
        }
    }

    /// <summary>托盘菜单的"显示"。</summary>
    public void ShowFromMenu()
    {
        IsUserHidden = false;
        UpdateVisibility();
    }

    /// <summary>托盘菜单的"隐藏"。</summary>
    public void HideFromMenu()
    {
        IsUserHidden = true;
        UpdateVisibility();
    }

    private void OnSessionAvailabilityChanged(object? sender, bool available)
    {
        // 事件来自后台线程
        try
        {
            Dispatcher.Invoke(UpdateVisibility);
        }
        catch (Exception ex)
        {
            Log.Warn("响应会话变化失败: " + ex.Message);
        }
    }

    // ---------------- 字号 ----------------

    /// <summary>加减号按钮与菜单共用：调整歌词字号并立刻重新排版。</summary>
    public void AdjustFontSize(double delta)
    {
        double next = Math.Min(MaxFontSize, Math.Max(MinFontSize, _settings.FontSize + delta));
        if (Math.Abs(next - _settings.FontSize) < 0.01) return;

        _settings.FontSize = next;
        _settings.Save();
        ApplySettings();
        Log.Info($"字号调整为 {next:0}");
    }

    // ---------------- 按钮栏（锁定 / 加 / 减）----------------

    /// <summary>
    /// 轮询光标位置判断是否悬停在浮窗上。
    /// 不能用 MouseEnter/MouseLeave：锁定后窗口是穿透的，收不到任何鼠标事件。
    /// </summary>
    private void UpdateButtonBarHover()
    {
        if (!TryGetMousePositionInDip(out Point cursor)) return;

        double localX = cursor.X - Left;
        double localY = cursor.Y - Top;
        bool inside = localX >= 0 && localX <= Width && localY >= 0 && localY <= Height;

        // 锁图标的点击区是否被光标覆盖 —— 决定这一小块要不要取消穿透
        bool overLockZone = inside && IsInLockZone(localX, localY);
        if (overLockZone != _cursorOverLockZone)
        {
            _cursorOverLockZone = overLockZone;
            UpdateClickThrough();
        }

        if (inside)
        {
            _cursorInside = true;
            if (!_buttonBarShown) ShowButtonBar(true);
            return;
        }

        if (_cursorInside)
        {
            _cursorInside = false;
            _cursorLeftAt = DateTime.UtcNow;
        }

        if (_buttonBarShown && DateTime.UtcNow - _cursorLeftAt >= ButtonBarHideDelay)
        {
            ShowButtonBar(false);
        }
    }

    private void ShowButtonBar(bool show)
    {
        _buttonBarShown = show;
        Log.Info($"浮窗按钮{(show ? "显示" : "隐藏")}（鼠标{(show ? "进入" : "离开超过 1 秒")}浮窗）");

        ButtonBar.BeginAnimation(OpacityProperty, new DoubleAnimation(
            show ? 0 : 1,
            show ? 1 : 0,
            TimeSpan.FromMilliseconds(show ? 120 : 200)));
    }

    // ---------------- 歌词渲染 ----------------

    public void SetLyrics(ResolvedLyrics lyrics)
    {
        _lyrics = lyrics;
        _lineIndex = -1;

        if (lyrics.Found && lyrics.Lines.Count > 0)
        {
            _showingLyrics = true;
            ContentPanel.Visibility = Visibility.Visible;
            StatusLine.Visibility = Visibility.Collapsed;

            _lineIndex = FindLineIndex(CurrentPosition());
            RenderCurrentLine(animate: false);
        }
        else
        {
            // 歌词搜不到时把"Playing: 歌名 - 歌手"当作正文显示（字号与歌词一致）
            SetStatus("Playing: " + _media.Current.DisplayName);
        }

        UpdateVisibility();
    }

    /// <summary>显示兜底/临时文字（小字）。传空字符串表示整块内容留空。</summary>
    public void SetStatus(string text)
    {
        _showingLyrics = false;
        ContentPanel.Visibility = Visibility.Collapsed;
        _lineIndex = -1;

        if (string.IsNullOrEmpty(text))
        {
            StatusLine.Text = string.Empty;
            StatusLine.Visibility = Visibility.Collapsed;
            LayoutWindowWidth(null, _settings.FontSize);
        }
        else
        {
            StatusLine.Text = text;
            StatusLine.FontSize = _settings.FontSize;
            StatusLine.Visibility = Visibility.Visible;
            LayoutWindowWidth(text, _settings.FontSize);
        }

        UpdateVisibility();
    }

    private TimeSpan CurrentPosition()
    {
        TimeSpan position = _media.CurrentPosition + TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        UpdateVisibility();
        if (Visibility != Visibility.Visible) return;

        // 每约 0.5 秒重申一次置顶：置顶是一整层，别的置顶窗口一被激活就会跑到前面
        if (++_tickCount >= 15)
        {
            _tickCount = 0;
            NativeMethods.RaiseToTopMost(_handle);
        }

        UpdateButtonBarHover();
        UpdateProgressBar();

        if (!_showingLyrics || _lyrics.Lines.Count == 0) return;

        int index = FindLineIndex(CurrentPosition());
        if (index != _lineIndex)
        {
            _lineIndex = index;
            RenderCurrentLine(animate: true);
        }
    }

    /// <summary>
    /// 更新歌曲进度条：已播放部分实心白，未播放部分只有 1px 白色描边，
    /// 交界处放光点（光点位置随进度走）。
    /// </summary>
    private void UpdateProgressBar()
    {
        TimeSpan duration = _media.Duration;
        if (!_media.HasSession || duration <= TimeSpan.Zero)
        {
            ProgressHost.Visibility = Visibility.Collapsed;
            return;
        }

        ProgressHost.Visibility = Visibility.Visible;

        double ratio = _media.CurrentPosition.TotalMilliseconds / duration.TotalMilliseconds;
        if (ratio < 0) ratio = 0;
        else if (ratio > 1) ratio = 1;

        // 条宽固定 240；左右各 1px 是描边，填充只画在内侧
        double inner = Math.Max(0, ProgressBarWidth - 2);
        double filled = inner * ratio;

        // 位置没变到 1 像素就不动布局，省得 30fps 一直触发布局
        int rounded = (int)Math.Round(filled);
        if (rounded == _lastProgressPixels) return;
        _lastProgressPixels = rounded;

        ProgressFill.Width = filled;
        Canvas.SetLeft(ProgressGlow, 1 + filled - ProgressGlow.Width / 2);
        Canvas.SetLeft(ProgressSpark, 1 + filled - ProgressSpark.Width / 2);
    }

    /// <summary>二分找"最后一条时间 &lt;= 当前位置"的歌词行。</summary>
    private int FindLineIndex(TimeSpan position)
    {
        var lines = _lyrics.Lines;
        if (lines.Count == 0) return -1;

        int low = 0, high = lines.Count - 1, result = 0;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (lines[mid].Time <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return result;
    }

    private void RenderCurrentLine(bool animate)
    {
        var lines = _lyrics.Lines;
        int index = _lineIndex;
        if (index < 0 || index >= lines.Count) return;

        LyricLine current = lines[index];

        // 恒定字号：不再按行缩放，长句交给窗口向两侧撑开
        CurrentLine.FontSize = _settings.FontSize;
        CurrentLine.Text = current.Text;
        LayoutWindowWidth(current.Text, _settings.FontSize);

        bool showTranslation = _settings.ShowTranslation && current.HasTranslation;
        TranslationLine.Text = showTranslation ? current.Translation! : string.Empty;
        TranslationLine.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        if (showTranslation)
        {
            TranslationLine.FontSize = Math.Max(MinFontSize - 1, _settings.FontSize * 0.52);
        }

        if (animate)
        {
            AnimateLineChange();
        }
    }

    /// <summary>
    /// 按文字实际宽度决定窗口宽度：基础宽度是设置里的 480，长句就向左右撑开，
    /// 撑开时保持窗口中心不动（所以两侧是同时变宽的）。
    /// </summary>
    private void LayoutWindowWidth(string? text, double fontSize)
    {
        double measured = string.IsNullOrEmpty(text) ? 0 : MeasureTextWidth(text, fontSize);
        double desired = Math.Max(_settings.Width, measured + LineSidePadding * 2);

        // 别撑出屏幕
        double maxWidth = Math.Max(_settings.Width, SystemParameters.WorkArea.Width - 40);
        desired = Math.Min(desired, maxWidth);

        SetWindowWidth(desired);
    }

    private void SetWindowWidth(double desiredWidth)
    {
        double scale = DpiScale();

        // 宽度取偶数个物理像素：这样 Width/2 在物理像素上是整数，
        // 中心不会落在半个像素上，按钮也就不会因为奇偶而在 1px 上下抖。
        double physical = Math.Round(desiredWidth * scale);
        if (((long)physical & 1) != 0) physical += 1;
        if (physical < 16) physical = 16;

        double newWidth = physical / scale;
        if (Math.Abs(Width - newWidth) < 1e-6) return;

        if (!_centerAnchorReady) CaptureCenterAnchor();

        if (!_centerAnchorReady)
        {
            Width = newWidth; // 还没有锚点（理论上不会），退化为只改宽度
            return;
        }

        // 用锚点反算 Left：中心恒定，宽度变化只影响两侧各伸出去多少
        Width = newWidth;
        Left = (_centerAnchorPhysical - physical / 2.0) / scale;
    }

    private double DpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return scale > 0 ? scale : 1.0;
    }

    /// <summary>记录"框中心"的物理像素位置（整数）。位置变动（启动恢复、拖动结束）后需要重新记录。</summary>
    private void CaptureCenterAnchor()
    {
        if (double.IsNaN(Left) || Width <= 0) return;

        _centerAnchorPhysical = (int)Math.Round((Left + Width / 2.0) * DpiScale());
        _centerAnchorReady = true;
    }

    private double MeasureTextWidth(string text, double fontSize)
    {
        var typeface = new Typeface(
            CurrentLine.FontFamily, CurrentLine.FontStyle, CurrentLine.FontWeight, CurrentLine.FontStretch);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>换行时的短淡入 + 轻微上移。只切一行，不做滚动。</summary>
    private void AnimateLineChange()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        var slide = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease };

        CurrentLine.BeginAnimation(OpacityProperty, fade);
        CurrentLineOffset.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // ---------------- 鼠标交互（仅在未锁定时有效）----------------

    private bool CanInteract => !_settings.Locked;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanInteract) return;

        _dragging = true;
        _dragStartMouse = GetMousePositionInDip();
        _dragOriginLeft = Left;
        _dragOriginTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        Point now = GetMousePositionInDip();
        Left = _dragOriginLeft + (now.X - _dragStartMouse.X);
        Top = _dragOriginTop + (now.Y - _dragStartMouse.Y);
    }

    /// <summary>
    /// 光标当前位置，单位换成 DIP。
    /// 注意：这里刻意不用 e.GetPosition(this)。鼠标相对窗口的坐标会随窗口移动而变化，
    /// 用它做增量会形成反馈回路，实测结果是窗口只跟着鼠标走一半的距离（拖起来发飘）。
    /// 屏幕坐标与窗口位置无关，因此位移是严格 1:1 的。
    /// </summary>
    private Point GetMousePositionInDip()
    {
        if (TryGetMousePositionInDip(out Point point)) return point;

        // 兜底：理论上不会走到，至少还能动
        Point fallback = Mouse.GetPosition(this);
        return new Point(_dragOriginLeft + fallback.X, _dragOriginTop + fallback.Y);
    }

    /// <summary>把光标位置换算成 DIP；失败返回 false。</summary>
    private bool TryGetMousePositionInDip(out Point point)
    {
        if (!NativeMethods.TryGetCursorPosition(out NativeMethods.POINT cursor))
        {
            point = default;
            return false;
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            point = new Point(cursor.X, cursor.Y);
            return false;
        }

        // 物理像素 -> DIP
        point = source.CompositionTarget.TransformFromDevice.Transform(new Point(cursor.X, cursor.Y));
        return true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        SaveBounds();
        CaptureCenterAnchor(); // 拖完位置变了，锚点跟着更新
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanInteract) return;
        e.Handled = true;
        ContextMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!CanInteract) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        AdjustFontSize(e.Delta > 0 ? +2 : -2);
        e.Handled = true;
    }
}
