using System;
using System.Collections.Generic;

namespace QQMusicLyrics.Models;

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

    /// <summary>带时间轴的歌词行数（用于日志与"是否匹配成功"的判断）。</summary>
    public int TimedLineCount { get; init; }
}
