using System;
using System.Globalization;
using System.Windows;
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
/// 默认只有一行歌词，换行时直接切换（不滚动、不显示上下句），没有任何背景板。
///
/// 显示规则：只有存在 QQ 音乐媒体会话时才显示。浏览器放 B 站视频同样会产生
/// SMTC 会话，但采集层只认 QQMusic.exe，所以这里 HasSession 为 false 时浮窗完全隐藏。
///
/// 锁定语义：锁定 = 同时开启鼠标穿透。因此锁定后浮窗收不到任何鼠标事件，
/// 解锁只能通过托盘菜单；浮窗上的锁定键只在未锁定状态下可点。
/// 悬停判定不依赖窗口事件（穿透时收不到），而是轮询光标位置。
/// </summary>
public partial class LyricsWindow : Window
{
    /// <summary>歌词左右留白（要与 XAML 里 ContentPanel 的 Margin 保持一致），用于计算可用宽度。</summary>
    private const double LineSidePadding = 16;

    /// <summary>自动缩字的下限，再长就交给省略号。</summary>
    private const double MinAutoFontSize = 12;

    /// <summary>鼠标离开浮窗多久后隐藏锁定键。</summary>
    private static readonly TimeSpan LockButtonHideDelay = TimeSpan.FromSeconds(2);

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

    // 锁定键的悬停状态
    private bool _cursorInside;
    private bool _lockButtonShown;
    private DateTime _cursorLeftAt = DateTime.UtcNow;

    /// <summary>请求弹出右键菜单（由托盘统一提供菜单，避免两套菜单不一致）。</summary>
    public event EventHandler? ContextMenuRequested;

    /// <summary>用户点了浮窗上的锁定键。</summary>
    public event EventHandler? LockToggleRequested;

    /// <summary>通过滚轮等方式改了设置，通知外层持久化。</summary>
    public event EventHandler? SettingsChanged;

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
            Interval = TimeSpan.FromMilliseconds(33), // ~30fps，用于换行判断与锁定键悬停检测
        };
        _ticker.Tick += OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            _ready = true;
            ApplySettings();
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

        LockButton.MouseLeftButtonDown += OnLockButtonClick;

        SetStatus(string.Empty);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyOverlayStyles(_handle);
        NativeMethods.SetClickThrough(_handle, _settings.Locked);
    }

    // ---------------- 外观 ----------------

    public void ApplySettings()
    {
        Width = _settings.Width;
        Height = _settings.Height;
        Opacity = _settings.WindowOpacity;
        Topmost = true;

        StatusLine.FontSize = Math.Max(MinAutoFontSize, _settings.FontSize * 0.62);

        UpdateLockAppearance();

        // 锁定 = 鼠标穿透
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.SetClickThrough(_handle, _settings.Locked);
        }

        // 让字号、翻译开关这类改动立刻反映到画面上
        if (_showingLyrics && _lineIndex >= 0)
        {
            RenderCurrentLine(animate: false);
        }

        UpdateVisibility();
    }

    /// <summary>根据锁定状态切换锁图标：未锁定显示开着的锁，锁定显示关着的锁。</summary>
    private void UpdateLockAppearance()
    {
        bool locked = _settings.Locked;
        LockShackleClosed.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        LockShackleOpen.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
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
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Height = Height;
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

    // ---------------- 锁定键 ----------------

    private void OnLockButtonClick(object sender, MouseButtonEventArgs e)
    {
        // 标记已处理，避免同时触发窗口拖动
        e.Handled = true;
        LockToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 轮询光标位置判断是否悬停在浮窗上。
    /// 不能用 MouseEnter/MouseLeave：锁定后窗口是穿透的，收不到任何鼠标事件。
    /// </summary>
    private void UpdateLockButtonHover()
    {
        if (!TryGetMousePositionInDip(out Point cursor)) return;

        bool inside = cursor.X >= Left && cursor.X <= Left + Width &&
                      cursor.Y >= Top && cursor.Y <= Top + Height;

        if (inside)
        {
            _cursorInside = true;
            if (!_lockButtonShown) ShowLockButton(true);
            return;
        }

        if (_cursorInside)
        {
            _cursorInside = false;
            _cursorLeftAt = DateTime.UtcNow;
        }

        if (_lockButtonShown && DateTime.UtcNow - _cursorLeftAt >= LockButtonHideDelay)
        {
            ShowLockButton(false);
        }
    }

    private void ShowLockButton(bool show)
    {
        _lockButtonShown = show;
        Log.Info($"锁定键{(show ? "显示" : "隐藏")}（鼠标{(show ? "进入" : "离开超过 2 秒")}浮窗）");

        if (show)
        {
            LockButton.Visibility = Visibility.Visible;
            LockButton.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            if (!_lockButtonShown) // 淡出过程中可能又被鼠标移入
            {
                LockButton.Visibility = Visibility.Collapsed;
            }
        };
        LockButton.BeginAnimation(OpacityProperty, fade);
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
            // 没匹配到歌词时至少把歌名显示出来，方便判断是不是匹配错了
            SetStatus(_media.Current.DisplayName);
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
        }
        else
        {
            StatusLine.Text = text;
            StatusLine.FontSize = Math.Max(MinAutoFontSize, _settings.FontSize * 0.62);
            StatusLine.Visibility = Visibility.Visible;
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

        UpdateLockButtonHover();

        if (!_showingLyrics || _lyrics.Lines.Count == 0) return;

        int index = FindLineIndex(CurrentPosition());
        if (index != _lineIndex)
        {
            _lineIndex = index;
            RenderCurrentLine(animate: true);
        }
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
        CurrentLine.Text = current.Text;
        FitCurrentLineToWidth();

        bool showTranslation = _settings.ShowTranslation && current.HasTranslation;
        TranslationLine.Text = showTranslation ? current.Translation! : string.Empty;
        TranslationLine.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        if (showTranslation)
        {
            TranslationLine.FontSize = Math.Max(MinAutoFontSize - 1, CurrentLine.FontSize * 0.52);
        }

        if (animate)
        {
            AnimateLineChange();
        }
    }

    /// <summary>
    /// 把当前行缩到能塞进窗口宽度。单行浮窗不能让长句换行或溢出，
    /// 所以按实测文本宽度等比缩小字号，缩到下限还超长就交给省略号。
    /// </summary>
    private void FitCurrentLineToWidth()
    {
        double configured = _settings.FontSize;
        string text = CurrentLine.Text;

        if (string.IsNullOrEmpty(text))
        {
            CurrentLine.FontSize = configured;
            return;
        }

        double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        double available = Math.Max(40, windowWidth - LineSidePadding * 2);
        double size = configured;

        double measured = MeasureTextWidth(text, size);
        if (measured > available)
        {
            size = Math.Max(MinAutoFontSize, size * (available / measured));
        }

        CurrentLine.FontSize = size;
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

        _settings.FontSize = Math.Min(120, Math.Max(12, _settings.FontSize + (e.Delta > 0 ? 2 : -2)));
        ApplySettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
