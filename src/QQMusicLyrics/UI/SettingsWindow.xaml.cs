using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using QQMusicLyrics.Models;
using QQMusicLyrics.Settings;

namespace QQMusicLyrics.UI;

/// <summary>
/// 设置窗口：左侧大类竖栏 + 右侧内容。目前只有一个「快捷键」大类。
///
/// 快捷键输入框的交互（按需求）：
///   点一下方框 → 边框冒出白色光晕表示进入"等待按键"状态；
///   按下组合键时实时显示；**松开所有键**时结束并生效；
///   或者**按下第三个键**时立刻结束（最多支持 3 个键）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>当前生效的快捷键。</summary>
    private HotkeyGesture _gesture;

    private bool _capturing;
    private readonly List<Key> _pressed = new();

    /// <summary>快捷键变更（已在窗口内写回 settings，由上层负责重新注册）。</summary>
    public event EventHandler<HotkeyGesture>? HotkeyChanged;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _gesture = HotkeyGesture.ParseOrDefault(settings.ToggleHotkey);
        HotkeyText.Text = _gesture.Display;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;

        // 拖到任务栏之外的位置也能用：限制在屏幕内
        Loaded += (_, _) => ClampToScreen();
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

        // 白色高亮：把底色压暗一点、边框变白再加一圈白色光晕。
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

        HotkeyBox.Background = Brushes.White;
        HotkeyBox.BorderBrush = Brushes.Black;
        HotkeyBox.BorderThickness = new Thickness(1);
        HotkeyBox.ClearValue(EffectProperty);

        HotkeyText.Text = _gesture.Display;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return;

        // 已经按满 3 个键就等这次结束
        if (_pressed.Count >= 3) return;
        if (!_pressed.Contains(key)) _pressed.Add(key);

        HotkeyText.Text = DescribePressed();
        if (_pressed.Count >= 3) Commit();
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        _pressed.Remove(key);

        // 全部松开 -> 确定
        if (_pressed.Count == 0) Commit();
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
                    mainKey = key; // 后面再按的非修饰键会覆盖前面的
                    break;
            }
        }

        if (mainKey == Key.None) return null;
        return new HotkeyGesture(modifiers, mainKey);
    }

    private string DescribePressed()
    {
        HotkeyGesture? gesture = BuildGesture();
        if (gesture is { } value) return value.Display;

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

    /// <summary>结束输入并应用。没有主键（只按了修饰键）时放弃这次修改。</summary>
    private void Commit()
    {
        HotkeyGesture? gesture = BuildGesture();
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
