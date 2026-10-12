using System;
using System.Threading;
using System.Threading.Tasks;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Models;
using Windows.Media.Control;

namespace QQMusicLyrics.Media;

/// <summary>
/// 采集层：通过 Windows 系统媒体传输控件（SMTC）读取"QQ 音乐当前在听什么"。
///
/// 这是整个方案"低侵入"的关键——它只是向操作系统查询媒体会话，
/// 不注入 QQ 音乐进程、不读它的内存、不 hook 它的网络、不改它的任何文件。
///
/// 只认 QQ 音乐：浏览器放 B 站视频时同样会注册 SMTC 会话，若直接用
/// <c>GetCurrentSession()</c> 就会把视频标题当成歌曲。所以这里遍历
/// <c>GetSessions()</c>，只挑 SourceAppUserModelId 为 QQMusic.exe 的那个；
/// 挑不到就表示"QQ 音乐没有可显示的内容"，上层据此完全不显示浮窗。
///
/// 位置获取策略（依据实测数据）：QQ 音乐每秒上报一次带 LastUpdatedTime 的时间轴，
/// 直接取 Position 会看到约 0.88 秒一格的台阶，所以以 LastUpdatedTime 为锚点外推，
/// 得到毫秒级连续位置。
/// </summary>
public sealed class SmtcMediaService : IDisposable
{
    /// <summary>连续失败多少次就认为会话已失效、需要重新发现。</summary>
    private const int FailureThreshold = 3;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _sessionAppId = string.Empty;

    /// <summary>会话代数。事件回调靠它判断自己是否已过期，从而不需要注销事件。</summary>
    private int _generation;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _disposed;
    private int _consecutiveFailures;

    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorTime;
    private TimeSpan _duration;
    private bool _hasTimeline;

    /// <summary>当前曲目（QQ 音乐没有会话时为 Empty）。</summary>
    public TrackInfo Current { get; private set; } = TrackInfo.Empty;

    /// <summary>是否存在 QQ 音乐的媒体会话。为 false 时上层应完全不显示浮窗。</summary>
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

    /// <summary>QQ 音乐会话出现或消失。true 表示出现。</summary>
    public event EventHandler<bool>? SessionAvailabilityChanged;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        SyncSession();

        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        Log.Info($"SMTC 采集已启动（只识别 {MediaSessionFilter.TargetExecutable}）");
    }

    // ---------------- 会话发现 ----------------

    /// <summary>从所有媒体会话里挑出 QQ 音乐的那个；没有就返回 null。</summary>
    private GlobalSystemMediaTransportControlsSession? FindTargetSession()
    {
        var manager = _manager;
        if (manager is null) return null;

        try
        {
            foreach (var session in manager.GetSessions())
            {
                if (MediaSessionFilter.IsQqMusic(session.SourceAppUserModelId)) return session;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("枚举媒体会话失败: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// 把绑定关系同步到"当前实际存在的 QQ 音乐会话"。
    /// 轮询里每次都调用，天然覆盖开始播放 / 停止播放 / QQ 音乐重启等情况。
    /// </summary>
    private void SyncSession()
    {
        var found = FindTargetSession();

        if (found is null)
        {
            if (_session is not null) AttachSession(null);
            return;
        }

        bool sameAsBound = _session is not null &&
            string.Equals(found.SourceAppUserModelId, _sessionAppId, StringComparison.OrdinalIgnoreCase);

        // 用 AUMID 判定而不是对象引用：WinRT 对同一会话是否返回同一个包装对象并无保证。
        if (!sameAsBound) AttachSession(found);
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        bool hadSession = _session is not null;
        _generation++;
        int generation = _generation;

        _session = session;
        _sessionAppId = session?.SourceAppUserModelId ?? string.Empty;
        _consecutiveFailures = 0;

        if (session is null)
        {
            lock (_gate)
            {
                _hasTimeline = false;
                _duration = TimeSpan.Zero;
            }
            IsPlaying = false;
            UpdateTrack(TrackInfo.Empty);
            if (hadSession)
            {
                Log.Info("QQ 音乐媒体会话已消失");
                SessionAvailabilityChanged?.Invoke(this, false);
            }
            return;
        }

        Log.Info($"已绑定 QQ 音乐会话: {session.SourceAppUserModelId}");

        session.MediaPropertiesChanged += (_, _) => { if (generation == _generation) _ = RefreshAsync(); };
        session.PlaybackInfoChanged += (_, _) => { if (generation == _generation) _ = RefreshAsync(); };
        session.TimelinePropertiesChanged += (_, _) => { if (generation == _generation) ReadTimeline(session); };

        ReadTimeline(session);
        SessionAvailabilityChanged?.Invoke(this, true);
    }

    /// <summary>读取时间轴并按 LastUpdatedTime 建立外推锚点。</summary>
    private void ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset lastUpdated = timeline.LastUpdatedTime;

            // 正常情况下 LastUpdatedTime 与当前时刻相差不到 1 秒；
            // 只有明显不合理（未来时间 / 过于陈旧）时才退回用当前时刻当锚点。
            bool usable = lastUpdated <= now && (now - lastUpdated) < TimeSpan.FromSeconds(20);

            lock (_gate)
            {
                _anchorPosition = timeline.Position;
                _anchorTime = usable ? lastUpdated : now;
                _duration = timeline.EndTime > timeline.StartTime
                    ? timeline.EndTime - timeline.StartTime
                    : timeline.EndTime;
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

    // ---------------- 轮询 ----------------

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                SyncSession();
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("轮询循环异常退出", ex);
        }
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

            int generation = _generation;
            var properties = await session.TryGetMediaPropertiesAsync();
            if (generation != _generation) return; // 期间换了会话，这次结果作废

            var playback = session.GetPlaybackInfo();
            bool playing = playback.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing != IsPlaying)
            {
                IsPlaying = playing;
                PlaybackStateChanged?.Invoke(this, playing);
            }

            UpdateTrack(new TrackInfo(
                properties.Title ?? string.Empty,
                properties.Artist ?? string.Empty,
                properties.AlbumTitle ?? string.Empty));

            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures == 1)
            {
                Log.Warn("刷新媒体信息失败: " + ex.Message);
            }
            if (_consecutiveFailures >= FailureThreshold)
            {
                // 会话多半已失效（QQ 音乐退出或重启），先解绑，下一轮轮询会重新发现。
                Log.Info($"媒体会话连续 {_consecutiveFailures} 次读取失败，重新发现会话");
                AttachSession(null);
            }
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

    // ---------------- 播放控制（SMTC 原生支持）----------------

    public async Task TogglePlayPauseAsync() => await SafeControlAsync("切换播放暂停",
        session => session.TryTogglePlayPauseAsync());

    public async Task NextAsync() => await SafeControlAsync("下一首",
        session => session.TrySkipNextAsync());

    public async Task PreviousAsync() => await SafeControlAsync("上一首",
        session => session.TrySkipPreviousAsync());

    private async Task SafeControlAsync(string action,
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> operation)
    {
        var session = _session;
        if (session is null)
        {
            Log.Warn($"无法{action}：当前没有 QQ 音乐会话");
            return;
        }

        try
        {
            bool ok = await operation(session);
            Log.Info($"{action}: {(ok ? "成功" : "被拒绝")}");
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
        // 释放它只会在退出时抛一个无关紧要的 ObjectDisposedException。
        try { _pollCts?.Cancel(); } catch { }

        _pollCts?.Dispose();
        _session = null;
        _manager = null;
    }
}
