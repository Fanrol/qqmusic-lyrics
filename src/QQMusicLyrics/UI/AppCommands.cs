using System;
using QQMusicLyrics.Interop;
using QQMusicLyrics.Media;
using QQMusicLyrics.Settings;

namespace QQMusicLyrics.UI;

/// <summary>
/// 托盘菜单和浮窗右键菜单共用的命令集，保证两处入口行为完全一致，也避免逻辑散落在 UI 里。
/// </summary>
public sealed class AppCommands
{
    private readonly AppSettings _settings;
    private readonly SmtcMediaService _media;
    private readonly LyricsWindow _window;
    private readonly Action _reloadLyrics;
    private readonly Action _exit;

    public AppCommands(
        AppSettings settings,
        SmtcMediaService media,
        LyricsWindow window,
        Action reloadLyrics,
        Action exit)
    {
        _settings = settings;
        _media = media;
        _window = window;
        _reloadLyrics = reloadLyrics;
        _exit = exit;
    }

    public AppSettings Settings => _settings;

    /// <summary>设置发生变化，托盘需要重新勾选状态。</summary>
    public event EventHandler? StateChanged;

    private void ApplyAndNotify()
    {
        _settings.Save();
        _window.ApplySettings();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 窗口行为 ----

    public void ToggleVisible()
    {
        _window.ToggleUserVisibility();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsVisible => !_window.IsUserHidden;

    public void ToggleClickThrough()
    {
        _settings.ClickThrough = !_settings.ClickThrough;
        ApplyAndNotify();
    }

    public void ToggleLocked()
    {
        _settings.Locked = !_settings.Locked;
        ApplyAndNotify();
    }

    public void ToggleHideWhenIdle()
    {
        _settings.HideWhenIdle = !_settings.HideWhenIdle;
        ApplyAndNotify();
    }

    // ---- 外观 ----

    public void ToggleTranslation()
    {
        _settings.ShowTranslation = !_settings.ShowTranslation;
        ApplyAndNotify();
    }

    public void ToggleNeighborLines()
    {
        _settings.ShowNeighborLines = !_settings.ShowNeighborLines;
        ApplyAndNotify();
    }

    public void ToggleBackdrop()
    {
        _settings.ShowBackdrop = !_settings.ShowBackdrop;
        ApplyAndNotify();
    }

    public void IncreaseFont() => ChangeFont(+2);
    public void DecreaseFont() => ChangeFont(-2);

    private void ChangeFont(double delta)
    {
        _settings.FontSize = Math.Min(120, Math.Max(14, _settings.FontSize + delta));
        ApplyAndNotify();
    }

    public void ResetFont()
    {
        _settings.FontSize = 34;
        ApplyAndNotify();
    }

    // ---- 歌词微调 ----

    public void ShiftLyrics(int milliseconds)
    {
        _settings.LyricOffsetMs = Math.Max(-10000, Math.Min(10000, _settings.LyricOffsetMs + milliseconds));
        ApplyAndNotify();
    }

    public void ResetLyricOffset()
    {
        _settings.LyricOffsetMs = 0;
        ApplyAndNotify();
    }

    public void ReloadLyrics() => _reloadLyrics();

    // ---- 播放控制（走 SMTC，不需要与 QQ 音乐直接通信）----

    public async void PlayPause() => await _media.TogglePlayPauseAsync();
    public async void NextTrack() => await _media.NextAsync();
    public async void PreviousTrack() => await _media.PreviousAsync();

    // ---- 系统集成 ----

    public void ToggleAutoStart()
    {
        bool target = !AutoStart.IsEnabled();
        if (AutoStart.Apply(target))
        {
            _settings.AutoStart = target;
            _settings.Save();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAutoStartEnabled => AutoStart.IsEnabled();

    public void Exit() => _exit();
}
