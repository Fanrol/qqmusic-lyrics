using System;
using System.Collections.Generic;
using System.Text;
using QQMusicLyrics.Models;

namespace QQMusicLyrics.Lyrics;

/// <summary>
/// 曲目匹配打分器。
///
/// 实测发现：smartbox 搜索把"歌名+歌手"一起作为关键词时反而常常 0 命中
/// （例如 "半抹烟熏 封茗囧菌" → 0 条，只搜 "半抹烟熏" → 1 条）。
/// 所以策略是：只用歌名搜，拿到候选后用歌手名打分挑出最合适的那条。
/// </summary>
public static class MatchScorer
{
    private const double AcceptThreshold = 0.50;

    public static SongCandidate? Pick(TrackInfo track, IEnumerable<SongCandidate> candidates)
    {
        var titleVariants = BuildTitleVariants(track.Title);
        var artists = SplitArtists(track.Artist);

        SongCandidate? best = null;
        double bestScore = 0;
        double bestTitleScore = 0;
        double bestArtistScore = 0;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Mid)) continue;

            string candidateTitle = Normalize(candidate.Name);
            string candidateSinger = Normalize(candidate.Singer);

            double titleScore = 0;
            foreach (var variant in titleVariants)
            {
                double s = Similarity(candidateTitle, variant);
                if (s > titleScore) titleScore = s;
            }

            double artistScore = ArtistScore(candidateSinger, artists);
            double score = titleScore * 0.65 + artistScore * 0.35;
            if (titleScore >= 0.999 && artistScore >= 0.999) score = 1.0;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestTitleScore = titleScore;
                bestArtistScore = artistScore;
            }
        }

        if (best is null || bestScore < AcceptThreshold)
        {
            return null;
        }

        // 标题对不上却因为歌手相同而"混进来"的情况要挡掉
        if (bestTitleScore < 0.45)
        {
            return null;
        }

        return best;
    }

    /// <summary>把歌词缓存键/搜索关键词统一成"去掉括号补充说明"的主标题。</summary>
    public static string StripBrackets(string title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        var sb = new StringBuilder(title.Length);
        int depth = 0;
        foreach (char ch in title)
        {
            if (ch is '（' or '(' or '【' or '[' or '〔' or '《')
            {
                depth++;
                continue;
            }
            if (ch is '）' or ')' or '】' or ']' or '〕' or '》')
            {
                if (depth > 0) depth--;
                continue;
            }
            if (depth == 0) sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>生成用于搜索/比较的歌名变体，按"可信度"从高到低。</summary>
    public static List<string> BuildTitleVariants(string title)
    {
        var variants = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = Normalize(value);
            if (normalized.Length == 0) return;
            if (!variants.Contains(normalized)) variants.Add(normalized);
        }

        Add(StripBrackets(title)); // 去掉 (我的爱不值一提吗) 这类补充说明，通常是索引里的正式名
        Add(title);
        return variants;
    }

    /// <summary>生成搜索关键词候选（去重，按优先级）。</summary>
    public static List<string> BuildSearchKeywords(string title, string artist)
    {
        var keywords = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string trimmed = value.Trim();
            if (trimmed.Length == 0) return;
            if (!keywords.Contains(trimmed)) keywords.Add(trimmed);
        }

        Add(StripBrackets(title));
        Add(title);
        if (!string.IsNullOrWhiteSpace(artist))
        {
            Add($"{StripBrackets(title)} {artist}");
            Add($"{title} {artist}");
        }
        return keywords;
    }

    public static List<string> SplitArtists(string? artists)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(artists)) return result;

        string[] parts = artists.Split(
            new[] { '/', '\\', '、', ',', '，', ';', '；', '&', '＆', '|', '+', '·' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            string normalized = Normalize(part);
            if (normalized.Length >= 2 && !result.Contains(normalized)) result.Add(normalized);
        }

        if (result.Count == 0)
        {
            string whole = Normalize(artists);
            if (whole.Length > 0) result.Add(whole);
        }
        return result;
    }

    private static double ArtistScore(string normalizedCandidateSinger, List<string> artists)
    {
        if (normalizedCandidateSinger.Length == 0 || artists.Count == 0) return 0;

        double best = 0;
        foreach (string artist in artists)
        {
            if (artist.Length == 0) continue;
            if (normalizedCandidateSinger.Contains(artist, StringComparison.Ordinal) ||
                artist.Contains(normalizedCandidateSinger, StringComparison.Ordinal))
            {
                best = Math.Max(best, 1.0);
                continue;
            }
            best = Math.Max(best, Dice(normalizedCandidateSinger, artist));
        }
        return best;
    }

    /// <summary>规范化：小写、去空白、去标点、全角转半角。这个尺度上的比较对中英文都够用。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch)) continue;
            char c = ToHalfWidth(char.ToLowerInvariant(ch));
            if (IsIgnorable(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsIgnorable(char c) =>
        c is '-' or '_' or '.' or ',' or '!' or '?' or '\'' or '"' or ':' or ';' or '~' or '^' or '*'
          or '·' or '、' or '。' or '，' or '！' or '？' or '：' or '；' or '“' or '”' or '‘' or '’'
          or '（' or '）' or '《' or '》' or '「' or '」' or '『' or '』' or '…' or '—' or '～';

    private static char ToHalfWidth(char c)
    {
        if (c == '\u3000') return ' ';
        if (c >= '\uFF01' && c <= '\uFF5E') return (char)(c - 0xFEE0);
        return c;
    }

    /// <summary>歌名相似度：完全相等最高，包含关系次之，其余用二元组 Dice 系数。</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1.0;

        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            // 包含关系可信，但长度差太多时打折（"晴天" 出现在 "晴天 (Live)" 里是好事，
            // 出现在某个长标题里就不一定了）
            int shorter = Math.Min(a.Length, b.Length);
            int longer = Math.Max(a.Length, b.Length);
            return 0.85 + 0.10 * ((double)shorter / longer);
        }

        return Dice(a, b);
    }

    /// <summary>二元组 Dice 系数，对中文歌名比编辑距离更稳。</summary>
    private static double Dice(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1.0;
        if (a.Length < 2 || b.Length < 2) return 0;

        var counts = new Dictionary<string, int>(a.Length);
        for (int i = 0; i < a.Length - 1; i++)
        {
            string gram = a.Substring(i, 2);
            counts[gram] = counts.TryGetValue(gram, out int n) ? n + 1 : 1;
        }

        int hits = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            string gram = b.Substring(i, 2);
            if (counts.TryGetValue(gram, out int n) && n > 0)
            {
                counts[gram] = n - 1;
                hits++;
            }
        }

        return 2.0 * hits / ((a.Length - 1) + (b.Length - 1));
    }
}
