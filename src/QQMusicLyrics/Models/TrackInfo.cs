using System;
using System.Collections.Generic;

namespace QQMusicLyrics.Models;

/// <summary>从 SMTC 读到的"当前在听什么"。</summary>
public sealed record TrackInfo(string Title, string Artist, string Album)
{
    public static readonly TrackInfo Empty = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} - {Artist}";

    /// <summary>用于歌词缓存的文件名（标题+歌手，避免同歌名不同歌手互相污染）。</summary>
    public string CacheKey => $"{Title}\u0001{Artist}".Trim().ToLowerInvariant();
}

/// <summary>smartbox 搜索返回的一个候选。</summary>
public sealed record SongCandidate(string Mid, string Name, string Singer);

/// <summary>歌曲详情。</summary>
public sealed record SongDetail(
    string Mid,
    string Title,
    string Singer,
    string AlbumMid,
    string AlbumName,
    TimeSpan Duration);

/// <summary>歌词接口的原始返回。</summary>
public sealed record LyricPayload(string Lyric, string Translation);

/// <summary>一行歌词（已把翻译并进来，便于 UI 直接消费）。</summary>
public sealed class LyricLine
{
    public LyricLine(TimeSpan time, string text, string? translation)
    {
        Time = time;
        Text = text;
        Translation = translation;
    }

    public TimeSpan Time { get; }
    public string Text { get; }
    public string? Translation { get; }

    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
}

/// <summary>补全后的完整结果：匹配到的曲目信息 + 逐行歌词 + 本地封面路径。</summary>
public sealed class ResolvedLyrics
{
    public static ResolvedLyrics Empty { get; } = new();

    public bool Found { get; init; }
    public string SongMid { get; init; } = string.Empty;
    public string MatchedTitle { get; init; } = string.Empty;
    public string MatchedArtist { get; init; } = string.Empty;
    public string AlbumName { get; init; } = string.Empty;
    public string AlbumMid { get; init; } = string.Empty;
    public string? CoverPath { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<LyricLine> Lines { get; init; } = Array.Empty<LyricLine>();

    /// <summary>歌词里带时间轴的行数（用于日志和"是否匹配成功"的判断）。</summary>
    public int TimedLineCount { get; init; }
}
