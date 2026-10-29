using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Lyrics;
using QQMusicLyrics.Media;
using QQMusicLyrics.Models;
using QQMusicLyrics.Settings;
using QQMusicLyrics.UI;

namespace QQMusicLyrics;

public partial class App : Application
{
    private const string MutexName = @"Local\QQMusicLyrics.SingleInstance";

    private Mutex? _mutex;
    private bool _ownsMutex;

    private AppSettings _settings = null!;
    private SmtcMediaService _media = null!;
    private LyricResolver _resolver = null!;
    private LyricsWindow _window = null!;
    private TrayIcon _tray = null!;
    private AppCommands _commands = null!;
    private CancellationTokenSource? _resolveCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(
                "QQ音乐浮窗歌词已经在运行了，请到系统托盘查看。",
                "QQ音乐浮窗歌词",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogFile);
        Log.Info("=== 启动 ===");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true; // 浮窗程序宁可少崩一次
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("域未处理异常", args.ExceptionObject as Exception);

        _settings = AppSettings.Load();
        _settings.Sanitize();

        _media = new SmtcMediaService();
        _resolver = new LyricResolver(_settings.CoverSize);
        _window = new LyricsWindow(_media, _settings);
        _commands = new AppCommands(_settings, _window, ExitApp);
        _tray = new TrayIcon(_commands);

        // 浮窗右键复用托盘菜单，避免维护两套
        _window.ContextMenuRequested += (_, _) => _tray.ShowMenu();

        // 浮窗上的锁定键与菜单里的"锁定/解锁"走同一条命令
        _window.LockToggleRequested += (_, _) => _commands.ToggleLocked();

        // 全局快捷键（默认 Alt+C）切换显示/隐藏
        _window.HotkeyPressed += (_, _) => _commands.ToggleVisibility();

        _media.TrackChanged += OnTrackChanged;

        _window.Show();
        _ = StartMediaAsync();
    }

    private async Task StartMediaAsync()
    {
        try
        {
            await _media.StartAsync();
            Log.Info("媒体采集就绪");
            // StartAsync 内部已经 await 过一次刷新，Current 与 TrackChanged 都已就绪，这里不需要再补触发。
        }
        catch (Exception ex)
        {
            Log.Error("启动 SMTC 采集失败", ex);
            _window.SetStatus("无法读取系统媒体信息");
        }
    }

    private void OnTrackChanged(object? sender, TrackInfo track)
    {
        _resolveCts?.Cancel();
        _resolveCts?.Dispose();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;

        if (track.IsEmpty)
        {
            // 没有 QQ 音乐会话：清空内容，浮窗会自己隐藏（见 LyricsWindow.UpdateVisibility）
            Dispatcher.Invoke(() =>
            {
                _window.SetStatus(string.Empty);
                _tray.UpdateTrackInfo(track, null);
            });
            return;
        }

        _ = ResolveAndApplyAsync(track, cts.Token);
    }

    private async Task ResolveAndApplyAsync(TrackInfo track, CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            _window.SetStatus("正在匹配歌词…");
            _tray.UpdateTrackInfo(track, null);
        });

        ResolvedLyrics result;
        try
        {
            result = await _resolver.ResolveAsync(track, token);
        }
        catch (OperationCanceledException)
        {
            return; // 已经切到下一首，这次结果作废
        }
        catch (Exception ex)
        {
            Log.Error("匹配歌词失败", ex);
            result = ResolvedLyrics.Empty;
        }

        if (token.IsCancellationRequested) return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested) return;
            _window.SetLyrics(result);
            _tray.UpdateTrackInfo(track, result);
        });
    }

    private void ReloadLyrics()
    {
        TrackInfo track = _media.Current;
        if (track.IsEmpty) return;

        Log.Info("手动重新匹配歌词: " + track.DisplayName);
        _resolver.InvalidateCache(track);

        _resolveCts?.Cancel();
        _resolveCts?.Dispose();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;
        _ = ResolveAndApplyAsync(track, cts.Token);
    }

    private void ExitApp()
    {
        Log.Info("=== 退出（用户操作）===");
        try { _window.Close(); } catch { /* 关闭失败也要退出 */ }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _resolveCts?.Cancel(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _media?.Dispose(); } catch { }
        try { _resolver?.Dispose(); } catch { }
        try { _resolveCts?.Dispose(); } catch { }

        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        try { _mutex?.Dispose(); } catch { }

        base.OnExit(e);
    }
}
