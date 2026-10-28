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
///
/// 注意阈值很松：歌名完全一致就能拿到 0.65，单靠这一项就过 0.50 的线。
/// 也就是说**歌手完全不对也会被接受**（真实踩过的坑：Monster 被匹配成 BIGBANG 的韩语版）。
/// 因此上层不能"第一条及格的就定案"，必须把所有关键词的候选合起来比一遍。
///
/// 另一个坑：**同名不同版本**。搜索接口返回的名字往往不带版本标记（"Daisy Crown"），
/// 而真正的版本信息只在**歌曲详情**的 title 里（"Daisy Crown (日文版)"）。
/// 所以带版本限定的曲目必须靠 <see cref="ExtractQualifiers"/> 在详情阶段再筛一次。
/// </summary>
public static class MatchScorer
{
    private const double AcceptThreshold = 0.50;

    /// <summary>标题里可能出现的版本/编曲限定。</summary>
    public enum QualifierKind
    {
        English,
        Japanese,
        Chinese,
        Korean,
        Cantonese,
        Instrumental,
        Live,
        Remix,
        Acoustic,
        Piano,
        Karaoke,
        Demo,
        Cover,
    }

    /// <summary>括号内片段 → 限定类别的匹配表（小写、去空格后按 Contains 判断）。</summary>
    private static readonly (string Pattern, QualifierKind Kind)[] QualifierPatterns =
    {
        ("英文版", QualifierKind.English),
        ("英语版", QualifierKind.English),
        ("英文", QualifierKind.English),
        ("英语", QualifierKind.English),
        ("english", QualifierKind.English),
        ("日文版", QualifierKind.Japanese),
        ("日语版", QualifierKind.Japanese),
        ("日文", QualifierKind.Japanese),
        ("日语", QualifierKind.Japanese),
        ("japanese", QualifierKind.Japanese),
        ("中文版", QualifierKind.Chinese),
        ("国语版", QualifierKind.Chinese),
        ("国语", QualifierKind.Chinese),
        ("普通话", QualifierKind.Chinese),
        ("chinese", QualifierKind.Chinese),
        ("粤语", QualifierKind.Cantonese),
        ("韩文", QualifierKind.Korean),
        ("韩语", QualifierKind.Korean),
        ("korean", QualifierKind.Korean),
        ("伴奏", QualifierKind.Instrumental),
        ("纯音乐", QualifierKind.Instrumental),
        ("无人声", QualifierKind.Instrumental),
        ("offvocal", QualifierKind.Instrumental),
        ("instrumental", QualifierKind.Instrumental),
        ("karaoke", QualifierKind.Karaoke),
        ("卡拉ok", QualifierKind.Karaoke),
        ("现场", QualifierKind.Live),
        ("live", QualifierKind.Live),
        ("remix", QualifierKind.Remix),
        ("混音", QualifierKind.Remix),
        ("acoustic", QualifierKind.Acoustic),
        ("不插电", QualifierKind.Acoustic),
        ("钢琴", QualifierKind.Piano),
        ("piano", QualifierKind.Piano),
        ("demo", QualifierKind.Demo),
        ("翻唱", QualifierKind.Cover),
        ("cover", QualifierKind.Cover),
    };

    /// <summary>一条候选的评分结果。</summary>
    public readonly record struct ScoredCandidate(
        SongCandidate Candidate, double Score, double TitleScore, double ArtistScore, int QualifierAffinity = 0)
    {
        public bool Accepted => Score >= AcceptThreshold && TitleScore >= 0.45;

        /// <summary>版本关系：1 = 曲名里的版本标记与曲目一致，-1 = 冲突，0 = 无从判断。</summary>
        public int Affinity => QualifierAffinity;
    }

    /// <summary>一次评估的结果：选中的候选（不接受时为 null）与各分项得分，便于上层比较不同关键词的结果。</summary>
    public readonly record struct MatchResult(
        SongCandidate? Candidate, double Score, double TitleScore, double ArtistScore)
    {
        public bool Accepted => Candidate is not null;
    }

    public static SongCandidate? Pick(TrackInfo track, IEnumerable<SongCandidate> candidates)
        => Evaluate(track, candidates).Candidate;

    /// <summary>
    /// 把所有候选按"版本关系 → 综合分"降序排好（**不做接受/拒绝过滤**，调用方自己决定用哪条）。
    ///
    /// 排序时先看版本关系：曲目带版本限定（英文版/伴奏/Live…）时，
    /// 曲名里版本标记一致的那条优先于"没有标记"的，冲突的排到最后。
    /// 全量搜索返回的 songname 带版本标记，所以这一步就能把日文版/中文版排除掉。
    /// </summary>
    public static List<ScoredCandidate> Rank(TrackInfo track, IEnumerable<SongCandidate> candidates)
    {
        var titleVariants = BuildTitleVariants(track.Title);
        var artists = SplitArtists(track.Artist);
        var trackQualifiers = ExtractQualifiers(track.Title);
        var scored = new List<ScoredCandidate>();

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

            int affinity = 0;
            if (trackQualifiers.Count > 0)
            {
                var candidateQualifiers = ExtractQualifiers(candidate.Name);
                if (candidateQualifiers.Count > 0)
                {
                    affinity = trackQualifiers.Overlaps(candidateQualifiers) ? 1 : -1;
                }
            }

            scored.Add(new ScoredCandidate(candidate, score, titleScore, artistScore, affinity));
        }

        scored.Sort((a, b) =>
        {
            int byAffinity = b.QualifierAffinity.CompareTo(a.QualifierAffinity);
            return byAffinity != 0 ? byAffinity : b.Score.CompareTo(a.Score);
        });

        return scored;
    }

    /// <summary>从候选里挑出最合适的一条，并返回各项得分（不接受时 Candidate 为 null）。</summary>
    public static MatchResult Evaluate(TrackInfo track, IEnumerable<SongCandidate> candidates)
    {
        var ranked = Rank(track, candidates);
        if (ranked.Count == 0) return new MatchResult(null, 0, 0, 0);

        ScoredCandidate top = ranked[0];
        return top.Accepted
            ? new MatchResult(top.Candidate, top.Score, top.TitleScore, top.ArtistScore)
            : new MatchResult(null, top.Score, top.TitleScore, top.ArtistScore);
    }

    /// <summary>
    /// 取出标题里**括号内**的版本限定。
    /// 只看括号内是有意的：像 "Olivia" 里含 "live"、"Englishman" 里含 "english" 这类整词误判都能避免。
    /// </summary>
    public static HashSet<QualifierKind> ExtractQualifiers(string? title)
    {
        var result = new HashSet<QualifierKind>();
        if (string.IsNullOrEmpty(title)) return result;

        int depth = 0;
        var segment = new StringBuilder();

        foreach (char ch in title)
        {
            if (ch is '(' or '（' or '[' or '【' or '〔' or '《')
            {
                depth++;
                segment.Clear();
                continue;
            }

            if (ch is ')' or '）' or ']' or '】' or '〕' or '》')
            {
                if (depth > 0)
                {
                    depth--;
                    CollectFromSegment(segment.ToString(), result);
                    segment.Clear();
                }
                continue;
            }

            if (depth > 0) segment.Append(ch);
        }

        return result;
    }

    private static void CollectFromSegment(string segment, HashSet<QualifierKind> result)
    {
        if (segment.Length == 0) return;

        string lowered = segment.ToLowerInvariant().Replace(" ", string.Empty);
        foreach (var (pattern, kind) in QualifierPatterns)
        {
            if (lowered.Contains(pattern, StringComparison.Ordinal)) result.Add(kind);
        }
    }

    /// <summary>版本限定是否冲突：两边都有、且没有任何交集时算冲突（一边没有信息就不算）。</summary>
    public static bool QualifiersConflict(
        HashSet<QualifierKind> trackQualifiers, HashSet<QualifierKind> candidateQualifiers)
    {
        return trackQualifiers.Count > 0 &&
               candidateQualifiers.Count > 0 &&
               !trackQualifiers.Overlaps(candidateQualifiers);
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
