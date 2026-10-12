using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using QQMusicLyrics.Models;

namespace QQMusicLyrics.Lyrics;

/// <summary>
/// LRC 解析器。QQ 音乐歌词接口返回的是逐行时间轴（形如 [00:17.04]歌词），
/// 另外可选地返回一份同样带时间轴的翻译，这里按时间戳合并成一份数据给 UI 用。
/// </summary>
public static class LrcParser
{
    private static readonly Regex TimeTag = new(
        @"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);

    private static readonly Regex MetaTag = new(
        @"^\[(ti|ar|al|by|offset|kana|length|re|ve):(.*)\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>翻译匹配允许的时间误差（毫秒）。两份歌词的时间戳偶尔会差几个毫秒。</summary>
    private const int TranslationToleranceMs = 300;

    public static List<LyricLine> Parse(string? mainLrc, string? translationLrc)
    {
        var main = ParseRaw(mainLrc);
        if (main.Count == 0) return new List<LyricLine>();

        var translation = ParseRaw(translationLrc);
        long[] translationTimes = new long[translation.Count];
        var translationTexts = new Dictionary<long, string>(translation.Count);
        for (int i = 0; i < translation.Count; i++)
        {
            long key = (long)Math.Round(translation[i].Time.TotalMilliseconds);
            translationTimes[i] = key;
            translationTexts[key] = translation[i].Text;
        }
        Array.Sort(translationTimes);

        var result = new List<LyricLine>(main.Count);
        foreach (var (time, text) in main)
        {
            string? translated = null;
            if (translationTimes.Length > 0)
            {
                long key = (long)Math.Round(time.TotalMilliseconds);
                int index = Array.BinarySearch(translationTimes, key);
                if (index >= 0)
                {
                    translated = translationTexts[translationTimes[index]];
                }
                else
                {
                    // 没精确命中就在相邻项里找最接近的，超过容差就当作这条没有翻译
                    int insert = ~index;
                    long bestDelta = long.MaxValue;
                    long bestTime = 0;
                    if (insert - 1 >= 0)
                    {
                        long delta = Math.Abs(translationTimes[insert - 1] - key);
                        if (delta < bestDelta) { bestDelta = delta; bestTime = translationTimes[insert - 1]; }
                    }
                    if (insert < translationTimes.Length)
                    {
                        long delta = Math.Abs(translationTimes[insert] - key);
                        if (delta < bestDelta) { bestDelta = delta; bestTime = translationTimes[insert]; }
                    }
                    if (bestDelta <= TranslationToleranceMs) translated = translationTexts[bestTime];
                }
            }
            result.Add(new LyricLine(time, text, translated));
        }
        return result;
    }

    /// <summary>解析成 (时间, 文本) 列表，已按时间排序并去掉空行与重复行。</summary>
    private static List<(TimeSpan Time, string Text)> ParseRaw(string? lrc)
    {
        var collected = new List<(TimeSpan Time, string Text)>();
        if (string.IsNullOrWhiteSpace(lrc)) return collected;

        int offsetMs = 0;
        var rawLines = lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (var rawLine in rawLines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            var meta = MetaTag.Match(line);
            if (meta.Success)
            {
                if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out offsetMs);
                }
                continue;
            }

            var matches = TimeTag.Matches(line);
            if (matches.Count == 0) continue; // 没有时间轴的行（纯文本歌词）直接忽略，避免错位

            string text = TimeTag.Replace(line, string.Empty).Trim();
            if (text.Length == 0) continue;   // 空行会让浮窗闪成空白，丢掉

            foreach (Match match in matches)
            {
                var time = ParseTime(match);
                if (time.HasValue) collected.Add((time.Value, text));
            }
        }

        if (offsetMs != 0)
        {
            var shift = TimeSpan.FromMilliseconds(offsetMs);
            for (int i = 0; i < collected.Count; i++)
            {
                var shifted = collected[i].Time - shift;
                if (shifted < TimeSpan.Zero) shifted = TimeSpan.Zero;
                collected[i] = (shifted, collected[i].Text);
            }
        }

        collected.Sort((a, b) => a.Time.CompareTo(b.Time));

        var deduped = new List<(TimeSpan Time, string Text)>(collected.Count);
        foreach (var item in collected)
        {
            if (deduped.Count > 0)
            {
                var last = deduped[^1];
                if (last.Time == item.Time && last.Text == item.Text) continue;
            }
            deduped.Add(item);
        }
        return deduped;
    }

    private static TimeSpan? ParseTime(Match match)
    {
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
        {
            return null;
        }

        string secondsRaw = match.Groups[2].Value.Replace(':', '.');
        if (!double.TryParse(secondsRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(minutes * 60_000 + seconds * 1000);
    }
}
