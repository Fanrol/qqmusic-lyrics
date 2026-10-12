namespace QQMusicLyrics.Models;

/// <summary>歌词接口的原始返回（未解析的 LRC 文本）。</summary>
public sealed record LyricPayload(string Lyric, string Translation);
