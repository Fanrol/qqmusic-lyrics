using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QQMusicLyrics.Interop;
using QQMusicLyrics.Media;
using QQMusicLyrics.Models;
using QQMusicLyrics.Settings;

namespace QQMusicLyrics.UI;

/// <summary>
/// 浮窗本体：无边框 + 逐像素透明 + 置顶 + 不出现在 Alt+Tab/任务栏。
/// 界面只负责"把当前这一行歌词画出来"，所有数据来自 SmtcMediaService 与 LyricResolver。
/// </summary>
public partial class LyricsWindow : Window
{
    private readonly SmtcMediaService _media;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _ticker;
    private readonly DateTime _startupGraceUntil = DateTime.Now.AddSeconds(15);

    private ResolvedLyrics _lyrics = ResolvedLyrics.Empty;
    private int _lineIndex = -1;
    private IntPtr _handle = IntPtr.Zero;

    private bool _dragging;
    private Point _dragStartMouse;   // 按下时鼠标的屏幕位置（DIP）
    private double _dragOriginLeft;
    private double _dragOriginTop;

    private bool _idleHidden;
    private string _transientStatus = string.Empty;

    /// <summary>请求弹出右键菜单（由托盘统一提供菜单，避免两套菜单不一致）。</summary>
    public event EventHandler? ContextMenuRequested;

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
            Interval = TimeSpan.FromMilliseconds(33), // ~30fps，只用于判断"该换行了"
        };
        _ticker.Tick += OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplySettings();
            _ticker.Start();
        };
        Closing += (_, _) => SaveBounds();

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += OnMouseRightButtonUp;
        MouseWheel += OnMouseWheel;

        ShowStatus("等待 QQ 音乐播放…");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyOverlayStyles(_handle);
        NativeMethods.SetClickThrough(_handle, _settings.ClickThrough);
    }

    // ---------------- 外观 ----------------

    public void ApplySettings()
    {
        Width = _settings.Width;
        Height = _settings.Height;
        Opacity = _settings.WindowOpacity;
        Topmost = true;

        CurrentLine.FontSize = _settings.FontSize;
        double neighbor = Math.Max(12, _settings.FontSize * _settings.NeighborFontScale);
        PrevLine.FontSize = neighbor;
        NextLine.FontSize = neighbor;
        TranslationLine.FontSize = Math.Max(12, _settings.FontSize * 0.60);
        StatusLine.FontSize = neighbor;

        Backdrop.Visibility = _settings.ShowBackdrop ? Visibility.Visible : Visibility.Collapsed;
        Backdrop.Opacity = _settings.BackdropOpacity;

        Visibility neighborVisibility = _settings.ShowNeighborLines ? Visibility.Visible : Visibility.Collapsed;
        PrevLine.Visibility = neighborVisibility;
        NextLine.Visibility = neighborVisibility;

        TranslationLine.Visibility = _settings.ShowTranslation && TranslationLine.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.SetClickThrough(_handle, _settings.ClickThrough);
        }

        // 让"显示翻译/上下行"这类开关立刻反映到画面上
        if (_lineIndex >= 0)
        {
            RenderCurrentLines(animate: false);
        }
    }

    private void RestorePosition()
    {
        // 这里必须全程使用 DIP（设备无关像素）。
        // 坑：WinForms 的 Screen.WorkingArea 返回物理像素，而 Window.Left/Top/Width/Height 都是 DIP。
        // 在 150% 缩放的屏幕上混用会把窗口算到屏幕外面（开发时实测：窗口跑到了 y=1842，屏幕只有 1600 高）。
        // SystemParameters.WorkArea 正好是 DIP 单位，直接用它就没有换算问题。
        Rect work = SystemParameters.WorkArea;

        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) &&
            IsOnAnyScreen(_settings.Left, _settings.Top))
        {
            Left = _settings.Left;
            Top = _settings.Top;
        }
        else
        {
            // 首次运行：屏幕底部居中，高度上离任务栏留一点距离
            Left = work.Left + (work.Width - Width) / 2.0;
            Top = work.Bottom - Height - 120;
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

    // ---------------- 歌词渲染 ----------------

    public void SetLyrics(ResolvedLyrics lyrics)
    {
        _lyrics = lyrics;
        _lineIndex = -1;

        if (lyrics.Found && lyrics.Lines.Count > 0)
        {
            _transientStatus = string.Empty;
            LyricsPanel.Visibility = Visibility.Visible;
            StatusLine.Visibility = Visibility.Collapsed;

            // 立即画一次，避免等到下一帧才出现
            _lineIndex = FindLineIndex(CurrentPosition());
            RenderCurrentLines(animate: false);
        }
        else
        {
            LyricsPanel.Visibility = Visibility.Collapsed;
            StatusLine.Visibility = Visibility.Visible;
            ShowStatus(_media.Current.IsEmpty
                ? "等待 QQ 音乐播放…"
                : $"{_media.Current.DisplayName}\n未找到歌词");
        }
    }

    public void ShowStatus(string text)
    {
        _transientStatus = text;
        StatusLine.Text = text;
        StatusLine.Visibility = Visibility.Visible;
        LyricsPanel.Visibility = Visibility.Collapsed;
    }

    private TimeSpan CurrentPosition()
    {
        TimeSpan position = _media.CurrentPosition + TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        UpdateIdleVisibility();

        if (_lyrics.Lines.Count == 0) return;
        if (LyricsPanel.Visibility != Visibility.Visible) return;

        int index = FindLineIndex(CurrentPosition());
        if (index != _lineIndex)
        {
            _lineIndex = index;
            RenderCurrentLines(animate: true);
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

    private void RenderCurrentLines(bool animate)
    {
        var lines = _lyrics.Lines;
        int index = _lineIndex;
        if (index < 0 || index >= lines.Count) return;

        PrevLine.Text = _settings.ShowNeighborLines && index > 0 ? lines[index - 1].Text : string.Empty;
        NextLine.Text = _settings.ShowNeighborLines && index + 1 < lines.Count ? lines[index + 1].Text : string.Empty;

        LyricLine current = lines[index];
        CurrentLine.Text = current.Text;

        bool showTranslation = _settings.ShowTranslation && current.HasTranslation;
        TranslationLine.Text = showTranslation ? current.Translation! : string.Empty;
        TranslationLine.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;

        if (animate)
        {
            AnimateLineChange();
        }
    }

    /// <summary>换行时的淡入 + 轻微上移，避免歌词"啪"地跳变。</summary>
    private void AnimateLineChange()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };

        CurrentLine.BeginAnimation(OpacityProperty, fade);
        CurrentLineOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    private void UpdateIdleVisibility()
    {
        bool idle = !_media.HasSession;
        bool shouldHide = IsUserHidden
                          || (_settings.HideWhenIdle && idle && DateTime.Now > _startupGraceUntil);

        if (shouldHide == _idleHidden) return;
        _idleHidden = shouldHide;
        Visibility = shouldHide ? Visibility.Hidden : Visibility.Visible;
    }

    public void ToggleUserVisibility()
    {
        IsUserHidden = !IsUserHidden;
        if (!IsUserHidden)
        {
            Visibility = Visibility.Visible;
            _idleHidden = false;
        }
        else
        {
            Visibility = Visibility.Hidden;
            _idleHidden = true;
        }
    }

    // ---------------- 鼠标交互 ----------------

    private bool CanInteract => !_settings.ClickThrough;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanInteract || _settings.Locked) return;

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
        if (!NativeMethods.TryGetCursorPosition(out NativeMethods.POINT cursor))
        {
            // 理论上不会失败；真失败就退回窗口相对坐标，至少还能动
            Point fallback = Mouse.GetPosition(this);
            return new Point(_dragOriginLeft + fallback.X, _dragOriginTop + fallback.Y);
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Point(cursor.X, cursor.Y);
        }

        // 物理像素 -> DIP
        return source.CompositionTarget.TransformFromDevice.Transform(new Point(cursor.X, cursor.Y));
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

        _settings.FontSize = Math.Min(120, Math.Max(14, _settings.FontSize + (e.Delta > 0 ? 2 : -2)));
        ApplySettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
