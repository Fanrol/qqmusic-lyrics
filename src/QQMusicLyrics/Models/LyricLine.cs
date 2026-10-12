using System;

namespace QQMusicLyrics.Models;

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
