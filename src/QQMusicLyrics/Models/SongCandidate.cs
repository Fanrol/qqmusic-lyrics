namespace QQMusicLyrics.Models;

/// <summary>smartbox 搜索返回的一个候选曲目。</summary>
public sealed record SongCandidate(string Mid, string Name, string Singer);
