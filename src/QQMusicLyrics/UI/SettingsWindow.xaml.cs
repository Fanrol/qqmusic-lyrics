using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using QQMusicLyrics.Models;
using QQMusicLyrics.Settings;

namespace QQMusicLyrics.UI;

/// <summary>
/// 设置窗口：左侧大类竖栏 + 右侧内容。目前只有一个「快捷键」大类。
///
/// 快捷键输入框的交互（按需求）：
///   点一下方框 → 边框冒出白色高亮表示进入"等待按键"状态；
///   按下组合键时实时显示；**松开所有键**时结束并生效；
///   或者**按下第三个键**时立刻结束（最多 3 个键）。
///
/// 注意：Windows 的 RegisterHotKey 只支持"修饰键 + 1 个主键"，
/// 所以同时按下两个非修饰键时只保留第一个（例如 Shift+H+J 会得到 Shift+H）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>当前生效的快捷键。</summary>
    private HotkeyGesture _gesture;

    private bool _capturing;
    private readonly List<Key> _pressed = new();

    /// <summary>本次输入里最近一次有效的组合。松开按键时用它来结算（松完时按下集合已空，不能现场再算）。</summary>
    private HotkeyGesture? _candidate;

    /// <summary>快捷键变更（已在窗口内写回 settings，由上层负责重新注册）。</summary>
    public event EventHandler<HotkeyGesture>? HotkeyChanged;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Icon = CreateWindowIcon();
        _gesture = HotkeyGesture.ParseOrDefault(settings.ToggleHotkey);
        HotkeyText.Text = _gesture.Display;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;

        Loaded += (_, _) => ClampToScreen();
    }

    /// <summary>任务栏/标题里显示的应用图标，和托盘图标同一套画法。</summary>
    private static ImageSource CreateWindowIcon()
    {
        using var bitmap = Glyphs.CreateAppIconBitmap(256);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ClampToScreen()
    {
        Rect work = SystemParameters.WorkArea;
        if (Left + Width > work.Right) Left = Math.Max(work.Left, work.Right - Width);
        if (Top + Height > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
        if (Left < work.Left) Left = work.Left;
        if (Top < work.Top) Top = work.Top;
    }

    // ---------------- 窗口按钮 / 拖动 ----------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void MinimizeButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    // ---------------- 快捷键输入 ----------------

    private void HotkeyBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginCapture();
    }

    private void BeginCapture()
    {
        _capturing = true;
        _pressed.Clear();
        _candidate = null;

        // 白色高亮：底色压暗一点、边框变白再加一圈白色光晕。
        // 纯白边框直接放在淡灰背景上几乎看不出来（实测截图确认），所以要制造明暗对比。
        HotkeyBox.Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4));
        HotkeyBox.BorderBrush = Brushes.White;
        HotkeyBox.BorderThickness = new Thickness(2);
        HotkeyBox.Effect = new DropShadowEffect
        {
            Color = Colors.White,
            BlurRadius = 26,
            ShadowDepth = 0,
            Opacity = 1,
        };

        HotkeyText.Text = "按下快捷键…";
        Activate();
        Focus();
    }

    private void EndCapture()
    {
        _capturing = false;
        _pressed.Clear();
        _candidate = null;

        HotkeyBox.Background = Brushes.White;
        HotkeyBox.BorderBrush = Brushes.Black;
        HotkeyBox.BorderThickness = new Thickness(1);
        HotkeyBox.ClearValue(EffectProperty);

        HotkeyText.Text = _gesture.Display;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftAlt or Key.RightAlt
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return;

        // 已经够 3 个键就忽略后续按键，等松开
        if (_pressed.Count >= 3) return;

        // 全局热键只支持"修饰键 + 一个主键"：已经有主键时，再来的非修饰键一律忽略
        if (!IsModifierKey(key) && _pressed.Exists(k => !IsModifierKey(k))) return;

        if (!_pressed.Contains(key)) _pressed.Add(key);

        HotkeyGesture? gesture = BuildGesture();
        if (gesture is { } value && !value.IsEmpty) _candidate = value;

        HotkeyText.Text = DescribePressed();

        // 按满三个键且已经有一个完整组合 -> 立刻确定
        if (_pressed.Count >= 3 && _candidate is not null) Commit(_candidate);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        _pressed.Remove(key);

        // 全部松开 -> 用刚才记下的组合结算
        if (_pressed.Count == 0) Commit(_candidate);
        else HotkeyText.Text = DescribePressed();
    }

    /// <summary>把当前按住的键变成手势；没有非修饰键时返回 null。</summary>
    private HotkeyGesture? BuildGesture()
    {
        var modifiers = HotkeyModifiers.None;
        Key mainKey = Key.None;

        foreach (Key key in _pressed)
        {
            switch (key)
            {
                case Key.LeftAlt:
                case Key.RightAlt:
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case Key.LeftCtrl:
                case Key.RightCtrl:
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case Key.LeftShift:
                case Key.RightShift:
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case Key.LWin:
                case Key.RWin:
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    mainKey = key;
                    break;
            }
        }

        if (mainKey == Key.None) return null;
        return new HotkeyGesture(modifiers, mainKey);
    }

    private string DescribePressed()
    {
        HotkeyGesture? gesture = BuildGesture();
        if (gesture is { } value && !value.IsEmpty) return value.Display;

        // 只按住了修饰键
        var parts = new List<string>();
        foreach (Key key in _pressed)
        {
            string? name = key switch
            {
                Key.LeftAlt or Key.RightAlt => "Alt",
                Key.LeftCtrl or Key.RightCtrl => "Ctrl",
                Key.LeftShift or Key.RightShift => "Shift",
                Key.LWin or Key.RWin => "Win",
                _ => null,
            };
            if (name is not null && !parts.Contains(name)) parts.Add(name);
        }
        return string.Join(" + ", parts) + " + …";
    }

    /// <summary>结束输入并应用。没有完整组合（只按了修饰键）时放弃这次修改。</summary>
    private void Commit(HotkeyGesture? gesture)
    {
        EndCapture();

        if (gesture is not { } value || value.IsEmpty) return;
        if (value == _gesture) return;

        _gesture = value;
        HotkeyText.Text = value.Display;
        _settings.ToggleHotkey = value.ToString();
        _settings.Save();
        HotkeyChanged?.Invoke(this, value);
    }
}
