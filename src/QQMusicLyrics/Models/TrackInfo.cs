using System;

namespace QQMusicLyrics.Models;

/// <summary>从 SMTC 读到的"QQ 音乐当前在听什么"。</summary>
public sealed record TrackInfo(string Title, string Artist, string Album)
{
    public static readonly TrackInfo Empty = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} - {Artist}";

    /// <summary>用于歌词缓存的文件名（标题+歌手，避免同歌名不同歌手互相污染）。</summary>
    public string CacheKey => $"{Title}\u0001{Artist}".Trim().ToLowerInvariant();
}
