using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Models;
using QQMusicLyrics.Net;

namespace QQMusicLyrics.Lyrics;

/// <summary>
/// 补全层：把"歌名 + 歌手"变成"带时间轴的歌词 + 封面"。
/// 流程：本地缓存 → 搜索候选 → 按歌手打分选曲 → 歌曲详情拿 albumMid → 歌词 → 封面 → 写缓存。
/// </summary>
public sealed class LyricResolver : IDisposable
{
    private readonly QQMusicClient _client = new();
    private readonly LyricCache _cache = new();
    private readonly int _coverSize;

    public LyricResolver(int coverSize)
    {
        _coverSize = coverSize;
    }

    public void InvalidateCache(TrackInfo track) => _cache.Invalidate(track);

    public async Task<ResolvedLyrics> ResolveAsync(TrackInfo track, CancellationToken token)
    {
        if (track.IsEmpty) return ResolvedLyrics.Empty;

        var cached = _cache.TryGet(track);
        if (cached is not null && !string.IsNullOrWhiteSpace(cached.Lyric))
        {
            var cachedLines = LrcParser.Parse(cached.Lyric, cached.Translation);
            if (cachedLines.Count > 0)
            {
                Log.Info($"歌词来自缓存: {cached.MatchedTitle} - {cached.MatchedArtist} ({cachedLines.Count} 行)");
                return Build(cached, cachedLines);
            }
        }

        token.ThrowIfCancellationRequested();

        var best = await FindBestMatchAsync(track, token).ConfigureAwait(false);
        if (best is null)
        {
            Log.Warn($"未能匹配到曲目: {track.DisplayName}");
            return ResolvedLyrics.Empty;
        }

        var detail = await _client.GetDetailAsync(best.Mid, token).ConfigureAwait(false);
        var payload = await _client.GetLyricAsync(best.Mid, token).ConfigureAwait(false);
        if (payload is null)
        {
            Log.Warn($"取歌词失败: mid={best.Mid}");
            return ResolvedLyrics.Empty;
        }

        var lines = LrcParser.Parse(payload.Lyric, payload.Translation);
        if (lines.Count == 0)
        {
            Log.Warn($"歌词解析后为空: mid={best.Mid}");
            return ResolvedLyrics.Empty;
        }

        var entry = new LyricCacheEntry
        {
            SongMid = best.Mid,
            MatchedTitle = detail?.Title is { Length: > 0 } t ? t : best.Name,
            MatchedArtist = detail?.Singer is { Length: > 0 } s ? s : best.Singer,
            AlbumName = detail?.AlbumName ?? string.Empty,
            AlbumMid = detail?.AlbumMid ?? string.Empty,
            DurationSeconds = detail?.Duration.TotalSeconds ?? 0,
            Lyric = payload.Lyric,
            Translation = payload.Translation,
            SavedAt = DateTimeOffset.Now,
        };

        _cache.Save(track, entry);

        Log.Info($"歌词匹配成功: '{track.DisplayName}' -> '{entry.MatchedTitle} - {entry.MatchedArtist}' " +
                 $"({lines.Count} 行, {lines.FindAll(l => l.HasTranslation).Count} 行翻译)");

        // 封面单独在后台取：它只是"顺手缓存下来备用"，绝不该拖慢或影响歌词上屏。
        var coverEntry = entry;
        _ = Task.Run(() => EnsureCoverAsync(coverEntry, CancellationToken.None));

        return Build(entry, lines);
    }

    /// <summary>按关键词优先级逐个搜索，一旦能挑出满意候选就立刻停下，避免多余请求。</summary>
    private async Task<SongCandidate?> FindBestMatchAsync(TrackInfo track, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var accumulated = new List<SongCandidate>();

        foreach (string keyword in MatchScorer.BuildSearchKeywords(track.Title, track.Artist))
        {
            token.ThrowIfCancellationRequested();

            var found = await _client.SearchAsync(keyword, token).ConfigureAwait(false);
            foreach (var candidate in found)
            {
                if (seen.Add(candidate.Mid)) accumulated.Add(candidate);
            }

            if (found.Count == 0) continue;

            var pick = MatchScorer.Pick(track, accumulated);
            if (pick is not null)
            {
                Log.Info($"匹配到候选: 关键词='{keyword}' -> '{pick.Name} - {pick.Singer}' (mid={pick.Mid})");
                return pick;
            }
        }

        return null;
    }

    private async Task EnsureCoverAsync(LyricCacheEntry entry, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entry.AlbumMid)) return;

            string path = CoverPathFor(entry.AlbumMid);
            if (File.Exists(path) && new FileInfo(path).Length > 1024) return;

            byte[]? bytes = await _client
                .DownloadAsync(QQMusicClient.BuildCoverUrl(entry.AlbumMid, _coverSize), "https://y.qq.com/", token)
                .ConfigureAwait(false);

            if (bytes is null || bytes.Length < 512)
            {
                Log.Warn("封面下载结果异常，跳过: " + entry.AlbumMid);
                return;
            }

            AppPaths.EnsureCreated();
            File.WriteAllBytes(path, bytes);
            Log.Info($"封面已缓存: {entry.AlbumMid} ({bytes.Length} 字节)");
        }
        catch (Exception ex)
        {
            // 封面失败不影响任何主流程
            Log.Warn("封面处理失败: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private string CoverPathFor(string albumMid) =>
        Path.Combine(AppPaths.CoverDir, $"{albumMid}_{_coverSize}.jpg");

    private ResolvedLyrics Build(LyricCacheEntry entry, List<LyricLine> lines)
    {
        string? coverPath = null;
        if (!string.IsNullOrWhiteSpace(entry.AlbumMid))
        {
            string candidate = CoverPathFor(entry.AlbumMid);
            if (File.Exists(candidate)) coverPath = candidate;
        }

        return new ResolvedLyrics
        {
            Found = true,
            SongMid = entry.SongMid,
            MatchedTitle = entry.MatchedTitle,
            MatchedArtist = entry.MatchedArtist,
            AlbumName = entry.AlbumName,
            AlbumMid = entry.AlbumMid,
            CoverPath = coverPath,
            Duration = TimeSpan.FromSeconds(entry.DurationSeconds),
            Lines = lines,
            TimedLineCount = lines.Count,
        };
    }

    public void Dispose() => _client.Dispose();
}
