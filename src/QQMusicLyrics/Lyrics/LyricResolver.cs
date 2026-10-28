using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Models;
using QQMusicLyrics.Net;

namespace QQMusicLyrics.Lyrics;

/// <summary>
/// 补全层：把"歌名 + 歌手"变成"带时间轴的歌词 + 封面"。
/// 流程：本地缓存 → 搜索候选 → 按歌手打分排序 → （带版本限定时校验详情标题）→ 歌曲详情拿 albumMid → 歌词 → 封面 → 写缓存。
/// </summary>
public sealed class LyricResolver : IDisposable
{
    /// <summary>带版本限定时，最多探测几条候选的详情（每探测一条要多一次请求）。</summary>
    private const int MaxVersionProbes = 4;

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

        var ranked = await CollectRankedCandidatesAsync(track, token).ConfigureAwait(false);
        var (best, detail) = await PickCandidateAsync(track, ranked, token).ConfigureAwait(false);
        if (best is null)
        {
            Log.Warn($"未能匹配到曲目: {track.DisplayName}");
            return ResolvedLyrics.Empty;
        }

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

    /// <summary>
    /// 按关键词优先级逐个搜索，**把所有关键词的候选合起来统一排序**。
    ///
    /// 这里曾经是"第一个关键词能挑出及格候选就停下"，结果踩了坑：
    /// 播放 "Monster - Alan Walker/Emyrson Flora" 时，只搜 "Monster" 会返回 BIGBANG / EXO / Shawn Mendes 等
    /// 同名歌（歌手分接近 0），但综合分 0.65 以上就"及格"了，于是直接定案，根本没去搜
    /// "Monster Alan Walker" —— 而那个关键词的第一条就是完美匹配（1.000）。
    ///
    /// 现在每个关键词搜完都重新排序；分数已经接近满分（>= <see cref="ConfidentScore"/>）才提前结束搜索。
    /// </summary>
    private async Task<List<MatchScorer.ScoredCandidate>> CollectRankedCandidatesAsync(
        TrackInfo track, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var accumulated = new List<SongCandidate>();
        var ranked = new List<MatchScorer.ScoredCandidate>();

        foreach (string keyword in MatchScorer.BuildSearchKeywords(track.Title, track.Artist))
        {
            token.ThrowIfCancellationRequested();

            var found = await _client.SearchAsync(keyword, token).ConfigureAwait(false);
            if (found.Count == 0) continue;

            foreach (var candidate in found)
            {
                if (seen.Add(candidate.Mid)) accumulated.Add(candidate);
            }

            ranked = MatchScorer.Rank(track, accumulated);
            var top = ranked.FirstOrDefault(s => s.Accepted);
            if (top.Candidate is not null)
            {
                Log.Info($"候选更新: 关键词='{keyword}' -> '{top.Candidate.Name} - {top.Candidate.Singer}' " +
                         $"(综合 {top.Score:0.000} = 标题 {top.TitleScore:0.000} + 歌手 {top.ArtistScore:0.000}, " +
                         $"mid={top.Candidate.Mid})");

                // 已经接近满分，再搜也不会有更好的了
                if (top.Score >= ConfidentScore) break;
            }
        }

        return ranked;
    }

    /// <summary>
    /// 从排好序的候选里选出最终要用的那一条。
    ///
    /// 关键点：**搜索接口返回的名字往往不带版本标记**（"Daisy Crown"），
    /// 真正的版本号只在歌曲详情的 title 里（"Daisy Crown (日文版)"）。
    /// 所以曲目本身带版本限定时（英文版 / 伴奏 / Live / Remix …），
    /// 必须多取几条详情、看完整标题再决定，否则会稳定地匹配到同名但语言/编曲不同的那条。
    /// </summary>
    private async Task<(SongCandidate? Candidate, SongDetail? Detail)> PickCandidateAsync(
        TrackInfo track, List<MatchScorer.ScoredCandidate> ranked, CancellationToken token)
    {
        var accepted = ranked.Where(s => s.Accepted).ToList();
        if (accepted.Count == 0) return (null, null);

        var trackQualifiers = MatchScorer.ExtractQualifiers(track.Title);

        // 曲目没有版本限定：保持原来的行为，直接用最高分那条（只花一次详情请求）
        if (trackQualifiers.Count == 0)
        {
            var top = accepted[0];
            Log.Info($"采用最高分候选: '{top.Candidate.Name} - {top.Candidate.Singer}' (mid={top.Candidate.Mid})");
            return (top.Candidate, await _client.GetDetailAsync(top.Candidate.Mid, token).ConfigureAwait(false));
        }

        // 全量搜索返回的曲名自带版本标记（"(英语)"、"(伴奏)"…）：
        // 排名第一那条的标记已经和曲目对上，就不必再逐条探详情了。
        if (accepted[0].Affinity == 1)
        {
            var exact = accepted[0];
            Log.Info($"采用版本一致的候选: '{exact.Candidate.Name} - {exact.Candidate.Singer}' (mid={exact.Candidate.Mid})");
            return (exact.Candidate, await _client.GetDetailAsync(exact.Candidate.Mid, token).ConfigureAwait(false));
        }

        // 曲目带版本限定：逐条看详情标题，跳过版本不符的
        var candidates = new List<(SongCandidate Candidate, SongDetail? Detail, HashSet<MatchScorer.QualifierKind> Qualifiers)>();

        foreach (var scored in accepted.Take(MaxVersionProbes))
        {
            token.ThrowIfCancellationRequested();

            var detail = await _client.GetDetailAsync(scored.Candidate.Mid, token).ConfigureAwait(false);
            string fullTitle = detail?.Title is { Length: > 0 } t ? t : scored.Candidate.Name;
            var qualifiers = MatchScorer.ExtractQualifiers(fullTitle);

            if (MatchScorer.QualifiersConflict(trackQualifiers, qualifiers))
            {
                Log.Info($"跳过版本不符的候选: '{fullTitle}'（曲目是「{track.Title}」）");
                continue;
            }

            candidates.Add((scored.Candidate, detail, qualifiers));
        }

        if (candidates.Count == 0)
        {
            // 一条都对不上就退回最高分，至少给点东西看
            var fallback = accepted[0];
            Log.Warn($"没有版本一致的候选，退回最高分: '{fallback.Candidate.Name} - {fallback.Candidate.Singer}'");
            return (fallback.Candidate, await _client.GetDetailAsync(fallback.Candidate.Mid, token).ConfigureAwait(false));
        }

        // 优先"版本完全对上"的，其次"没有版本标记"的，最后退回第一条
        var chosen = candidates.FirstOrDefault(c => c.Qualifiers.Overlaps(trackQualifiers));
        if (chosen.Candidate is null) chosen = candidates.FirstOrDefault(c => c.Qualifiers.Count == 0);
        if (chosen.Candidate is null) chosen = candidates[0];

        string chosenTitle = chosen.Detail?.Title is { Length: > 0 } ct ? ct : chosen.Candidate.Name;
        Log.Info($"采用版本一致的候选: '{chosenTitle} - {chosen.Candidate.Singer}' (mid={chosen.Candidate.Mid})");
        return (chosen.Candidate, chosen.Detail);
    }

    /// <summary>分数到这个程度就够了：继续换关键词搜也不太可能再提高。</summary>
    private const double ConfidentScore = 0.95;

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
