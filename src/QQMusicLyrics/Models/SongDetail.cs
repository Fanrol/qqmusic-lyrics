using System;

namespace QQMusicLyrics.Models;

/// <summary>歌曲详情（主要为拿 albumMid 以取封面）。</summary>
public sealed record SongDetail(
    string Mid,
    string Title,
    string Singer,
    string AlbumMid,
    string AlbumName,
    TimeSpan Duration);
