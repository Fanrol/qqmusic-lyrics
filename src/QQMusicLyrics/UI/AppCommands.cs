using System;
using QQMusicLyrics.Settings;

namespace QQMusicLyrics.UI;

/// <summary>
/// 托盘菜单与浮窗右键菜单共用的命令集：两处入口调同一套方法，行为不会不一致。
/// 目前只有"显示 / 隐藏 / 锁定 / 字号加减 / 退出"，其它能力等后续逐步加回。
/// </summary>
public sealed class AppCommands
{
    private readonly AppSettings _settings;
    private readonly LyricsWindow _window;
    private readonly Action _exit;

    public AppCommands(AppSettings settings, LyricsWindow window, Action exit)
    {
        _settings = settings;
        _window = window;
        _exit = exit;
    }

    public AppSettings Settings => _settings;

    /// <summary>设置或状态变化，菜单需要刷新文案与图标。</summary>
    public event EventHandler? StateChanged;

    public bool IsVisible => !_window.IsUserHidden;

    public bool IsLocked => _settings.Locked;

    public void ShowWindow()
    {
        _window.ShowFromMenu();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HideWindow()
    {
        _window.HideFromMenu();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换锁定。锁定 = 整窗鼠标穿透（锁图标本身除外）；解锁 = 取消穿透。</summary>
    public void ToggleLocked()
    {
        _settings.Locked = !_settings.Locked;
        _settings.Save();
        _window.ApplySettings(); // 这里会同步设置窗口的鼠标穿透样式
        Infra.Log.Info(_settings.Locked
            ? "已锁定：整窗鼠标穿透（把鼠标移到锁图标上可直接解锁）"
            : "已解锁：关闭鼠标穿透");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void IncreaseFont() => _window.AdjustFontSize(+2);

    public void DecreaseFont() => _window.AdjustFontSize(-2);

    public void Exit() => _exit();
}
