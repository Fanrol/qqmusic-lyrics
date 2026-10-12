using System;
using System.Threading;
using System.Threading.Tasks;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Models;
using Windows.Media.Control;

namespace QQMusicLyrics.Media;

/// <summary>
/// 采集层：通过 Windows 系统媒体传输控件（SMTC）读取"当前在听什么"。
///
/// 这是整个方案"低侵入"的关键——它只是向操作系统查询媒体会话，
/// 不注入 QQ 音乐进程、不读它的内存、不 hook 它的网络、不改它的任何文件。
///
/// 位置获取策略（依据实测数据）：QQ 音乐每秒上报一次带 LastUpdatedTime 的时间轴。
/// 直接取 Position 会看到 ~0.88s 一格的台阶，所以这里以 LastUpdatedTime 为锚点外推，
/// 得到毫秒级连续位置，供歌词逐行平滑滚动使用。
/// </summary>
public sealed class SmtcMediaService : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _disposed;

    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorTime;
    private TimeSpan _duration;
    private bool _hasTimeline;

    /// <summary>当前曲目（无会话时为 Empty）。</summary>
    public TrackInfo Current { get; private set; } = TrackInfo.Empty;

    public bool HasSession => _session is not null;

    public bool IsPlaying { get; private set; }

    public TimeSpan Duration
    {
        get { lock (_gate) { return _duration; } }
    }

    /// <summary>曲目变化（切歌 / 元数据刷新）。注意：在后台线程触发，订阅方需自行切回 UI 线程。</summary>
    public event EventHandler<TrackInfo>? TrackChanged;

    /// <summary>播放/暂停状态变化。</summary>
    public event EventHandler<bool>? PlaybackStateChanged;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) =>
        {
            var current = SafeGetCurrentSession();
            if (!ReferenceEquals(current, _session))
            {
                AttachSession(current);
                _ = RefreshAsync(); // 后续的会话切换用"触发即忘"即可
            }
        };

        AttachSession(SafeGetCurrentSession());

        // 首次必须 await：这样 StartAsync 返回时 Current 一定已经填好，
        // 上层不必再自己补一次触发（早期版本两边都触发，导致同一首歌被解析两遍）。
        await RefreshAsync();

        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        Log.Info("SMTC 采集已启动");
    }

    private GlobalSystemMediaTransportControlsSession? SafeGetCurrentSession()
    {
        try { return _manager?.GetCurrentSession(); }
        catch (Exception ex) { Log.Warn("GetCurrentSession 失败: " + ex.Message); return null; }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        _session = session;
        Log.Info(session is null ? "当前无媒体会话" : $"已绑定媒体会话: {session.SourceAppUserModelId}");

        if (session is null)
        {
            lock (_gate) { _hasTimeline = false; _duration = TimeSpan.Zero; }
            IsPlaying = false;
            UpdateTrack(TrackInfo.Empty);
            return;
        }

        var target = session;
        target.MediaPropertiesChanged += (_, _) => { if (ReferenceEquals(_session, target)) _ = RefreshAsync(); };
        target.PlaybackInfoChanged += (_, _) => { if (ReferenceEquals(_session, target)) _ = RefreshAsync(); };
        target.TimelinePropertiesChanged += (_, _) => { if (ReferenceEquals(_session, target)) ReadTimeline(target); };

        ReadTimeline(target);
    }

    /// <summary>读取时间轴并按 LastUpdatedTime 建立外推锚点。</summary>
    private void ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var tl = session.GetTimelineProperties();
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset lastUpdated = tl.LastUpdatedTime;

            // LastUpdatedTime 是"这个 Position 对应的时刻"。正常情况下它与当前时刻相差不到 1 秒。
            // 只有在明显不合理（未来时间 / 太旧）时才退回用当前时刻当锚点。
            bool usable = lastUpdated <= now && (now - lastUpdated) < TimeSpan.FromSeconds(20);

            lock (_gate)
            {
                _anchorPosition = tl.Position;
                _anchorTime = usable ? lastUpdated : now;
                _duration = tl.EndTime > tl.StartTime ? tl.EndTime - tl.StartTime : tl.EndTime;
                _hasTimeline = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取时间轴失败: " + ex.Message);
        }
    }

    /// <summary>基于锚点外推的当前位置。播放中才推进，暂停时冻结。</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            lock (_gate)
            {
                if (!_hasTimeline) return TimeSpan.Zero;
                TimeSpan position = _anchorPosition;
                if (IsPlaying) position += DateTimeOffset.Now - _anchorTime;
                if (_duration > TimeSpan.Zero && position > _duration) position = _duration;
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                return position;
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("轮询循环异常退出", ex); }
    }

    /// <summary>重新读取元数据与播放状态。事件触发和轮询都会调用，用信号量避免重入。</summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var session = _session;
            if (session is null)
            {
                UpdateTrack(TrackInfo.Empty);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing != IsPlaying)
            {
                IsPlaying = playing;
                PlaybackStateChanged?.Invoke(this, playing);
            }

            UpdateTrack(new TrackInfo(
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                props.AlbumTitle ?? string.Empty));
        }
        catch (Exception ex)
        {
            // 会话可能刚好关闭，这类错误属于正常竞争，不必刷屏
            Log.Warn("刷新媒体信息失败: " + ex.Message);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void UpdateTrack(TrackInfo info)
    {
        TrackInfo previous = Current;
        if (previous.Title == info.Title && previous.Artist == info.Artist && previous.Album == info.Album)
        {
            return;
        }

        Current = info;
        Log.Info($"曲目变化: '{info.Title}' / '{info.Artist}'" + (info.IsEmpty ? " (空)" : string.Empty));
        TrackChanged?.Invoke(this, info);
    }

    // ---- 播放控制（SMTC 原生支持，实测上一首/暂停/下一首均可用）----

    public async Task TogglePlayPauseAsync() => await SafeControlAsync("切换播放暂停",
        s => s.TryTogglePlayPauseAsync());

    public async Task NextAsync() => await SafeControlAsync("下一首",
        s => s.TrySkipNextAsync());

    public async Task PreviousAsync() => await SafeControlAsync("上一首",
        s => s.TrySkipPreviousAsync());

    private async Task SafeControlAsync(string action,
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> operation)
    {
        var session = _session;
        if (session is null)
        {
            Log.Warn($"无法{action}：当前没有媒体会话");
            return;
        }

        try
        {
            bool ok = await operation(session);
            Log.Info($"{action}: {(ok ? "成功" : "被拒绝")}");
            // 控制之后立即刷新，让界面尽快跟上
            _ = Task.Delay(150).ContinueWith(_ => RefreshAsync(), TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log.Error($"执行{action}失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 只取消轮询，不释放信号量：轮询任务此刻可能正卡在 WaitAsync 上，
        // 释放它只会在退出时抛一个无意义的 ObjectDisposedException。
        try { _pollCts?.Cancel(); } catch { }

        _pollCts?.Dispose();
        _session = null;
        _manager = null;
    }
}
