using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QQMusicLyrics.Lyrics;
using QQMusicLyrics.Media;
using QQMusicLyrics.Models;
using QQMusicLyrics.Net;
using Windows.Media.Control;

namespace QQMusicLyrics.SelfTest;

/// <summary>
/// 针对真实类的集成自测：直接引用主程序编译出来的程序集，不复制一份逻辑。
/// 覆盖会话筛选、LRC 解析、候选打分、QQ 接口、SMTC 采集与播放控制。
///
/// 用法：dotnet run --project tests\QQMusicLyrics.SelfTest
///      dotnet run --project tests\QQMusicLyrics.SelfTest -- --test-playback   （额外测试会真正暂停/恢复你的播放）
///      dotnet run --project tests\QQMusicLyrics.SelfTest -- --match "Monster|Alan Walker/Emyrson Flora"
///                                                             （用指定的曲目跑补全流程，不依赖当前在播什么）
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static bool _testPlayback;
    private static TrackInfo _forcedTrack = null;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 诊断用：只切换一次播放状态就退出（做对照测量前用它恢复播放）
        if (args.Any(a => a.Equals("--toggle-play", StringComparison.OrdinalIgnoreCase)))
        {
            await TogglePlaybackAsync();
            return 0;
        }

        _testPlayback = args.Any(a => a.Equals("--test-playback", StringComparison.OrdinalIgnoreCase));

        int matchIndex = Array.FindIndex(args, a => a.Equals("--match", StringComparison.OrdinalIgnoreCase));
        if (matchIndex >= 0 && matchIndex + 1 < args.Length)
        {
            string[] parts = args[matchIndex + 1].Split('|');
            string title = parts[0].Trim();
            string artist = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            if (title.Length > 0) _forcedTrack = new TrackInfo(title, artist, string.Empty);
        }

        Console.WriteLine("==================== 集成自测 ====================");
        Console.WriteLine(_testPlayback
            ? "（含播放控制测试：会短暂暂停再恢复你的播放）"
            : "（未启用播放控制测试；加 --test-playback 可启用）");
        if (_forcedTrack is not null)
        {
            Console.WriteLine($"（实况匹配固定用指定曲目：{_forcedTrack.DisplayName}）");
        }
        Console.WriteLine();

        TestLrcParser();
        TestMatchScorer();
        TestSessionFilter();
        await DumpLiveSessionsAsync();
        await TestQqClientAsync();
        await TestMatchAgainstLiveSearchAsync();
        await TestSmtcAsync();

        Console.WriteLine();
        Console.WriteLine($"==================== 结果: 通过 {_passed} / 失败 {_failed} ====================");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
        }
    }

    // ---------------- 1. 会话筛选（B 站视频不会被当成歌曲的保证）----------------
    private static void TestSessionFilter()
    {
        Console.WriteLine();
        Console.WriteLine("---- 1. 媒体会话筛选（只认 QQ 音乐）----");

        Check("纯文件名 QQMusic.exe", MediaSessionFilter.IsQqMusic("QQMusic.exe"));
        Check("大小写不敏感 qqmusic.exe", MediaSessionFilter.IsQqMusic("qqmusic.exe"));
        Check("完整路径", MediaSessionFilter.IsQqMusic(@"C:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe"));

        Check("拒绝 Edge（B 站场景）", !MediaSessionFilter.IsQqMusic("msedge.exe"));
        Check("拒绝 Chrome", !MediaSessionFilter.IsQqMusic("chrome.exe"));
        Check("拒绝 Firefox", !MediaSessionFilter.IsQqMusic("firefox.exe"));
        Check("拒绝打包应用 UWP 音乐", !MediaSessionFilter.IsQqMusic("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"));
        Check("拒绝随机播放器", !MediaSessionFilter.IsQqMusic("potplayer64.exe"));
        Check("拒绝空值", !MediaSessionFilter.IsQqMusic(null) && !MediaSessionFilter.IsQqMusic(""));
        Check("拒绝名字里包含但并非 QQ 音乐的可执行文件", !MediaSessionFilter.IsQqMusic("NotQQMusic.exe"));
    }

    // ---------------- 2. 现场枚举会话，展示筛选效果 ----------------
    private static async Task DumpLiveSessionsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("---- 2. 现场媒体会话（展示筛选结果）----");

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            Console.WriteLine($"  当前共有 {sessions.Count} 个媒体会话：");

            int accepted = 0;
            foreach (var session in sessions)
            {
                string appId = session.SourceAppUserModelId;
                bool isQq = MediaSessionFilter.IsQqMusic(appId);
                if (isQq) accepted++;

                var properties = await session.TryGetMediaPropertiesAsync();
                Console.WriteLine($"    {(isQq ? "[采用]" : "[忽略]")} {appId}  ->  {properties.Title} / {properties.Artist}");
            }

            Check("至少能枚举到会话", sessions.Count > 0, $"{sessions.Count} 个");
            Check("被采用的会话只可能是 QQ 音乐", accepted <= 1, $"采用 {accepted} 个");

            if (sessions.Count > accepted)
            {
                Check("存在非 QQ 音乐会话时确实被忽略", accepted < sessions.Count,
                    $"共 {sessions.Count} 个，忽略 {sessions.Count - accepted} 个");
            }
            else
            {
                Console.WriteLine("    （当前没有其它应用的媒体会话，无法现场验证忽略逻辑；已由第 1 节单测覆盖）");
            }
        }
        catch (Exception ex)
        {
            Check("枚举媒体会话", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---------------- 3. LRC 解析 ----------------
    private static void TestLrcParser()
    {
        Console.WriteLine();
        Console.WriteLine("---- 3. LRC 解析（含翻译合并、offset、多时间戳）----");

        const string main = "[ti:测试]\n[ar:歌手]\n[offset:0]\n" +
                            "[00:01.00]第一行\n" +
                            "[00:03.50]第二行\n" +
                            "[00:06.25][00:09.25]重复行\n" +
                            "[00:12.00]没有翻译的行\n";

        const string trans = "[00:01.02]First line\n" +
                             "[00:03.48]Second line\n" +
                             "[00:06.30]Repeated\n" +
                             "[00:09.20]Repeated\n";

        var lines = LrcParser.Parse(main, trans);

        Check("解析出 5 行", lines.Count == 5, $"实际 {lines.Count}");
        Check("首行时间 1.00s", lines.Count > 0 && Math.Abs(lines[0].Time.TotalSeconds - 1.0) < 0.001,
            lines.Count > 0 ? lines[0].Time.ToString() : "n/a");
        Check("翻译按时间戳合并", lines.Count > 0 && lines[0].Translation == "First line",
            lines.Count > 0 ? lines[0].Translation ?? "<null>" : "n/a");
        Check("第二行翻译合并", lines.Count > 1 && lines[1].Translation == "Second line",
            lines.Count > 1 ? lines[1].Translation ?? "<null>" : "n/a");
        Check("同一行多个时间戳展开", lines.Count(l => l.Text == "重复行") == 2);
        Check("无翻译的行 Translation 为空", lines.Count > 4 && lines[4].Translation is null);
        Check("空输入返回空列表", LrcParser.Parse("", "").Count == 0);

        var offsetLines = LrcParser.Parse("[offset:+500]\n[00:10.00]甲\n", null);
        Check("offset 生效（10s -> 9.5s）", offsetLines.Count == 1 &&
            Math.Abs(offsetLines[0].Time.TotalSeconds - 9.5) < 0.001,
            offsetLines.Count == 1 ? offsetLines[0].Time.ToString() : "n/a");
    }

    // ---------------- 4. 候选打分 ----------------
    private static void TestMatchScorer()
    {
        Console.WriteLine();
        Console.WriteLine("---- 4. 候选打分 ----");

        var cover = new List<SongCandidate> { new("003rBKBk4gQyzt", "半抹烟熏", "糯米幸sachiko") };
        Check("同名翻唱仍会选中（否则无歌词可用）",
            MatchScorer.Pick(new TrackInfo("半抹烟熏", "封茗囧菌", "半抹烟熏"), cover) is not null);

        var mixed = new List<SongCandidate>
        {
            new("a", "夜曲-周杰伦", "泊翔"),
            new("b", "夜曲", "周杰伦"),
        };
        Check("优先选原唱而非翻唱",
            MatchScorer.Pick(new TrackInfo("夜曲", "周杰伦", "十一月的萧邦"), mixed)?.Mid == "b");

        Check("去掉括号补充说明后能匹配",
            MatchScorer.Pick(new TrackInfo("雨是神的烟花 (我的爱不值一提吗)", "Nyxx.", ""),
                new List<SongCandidate> { new("m1", "雨是神的烟花", "Nyxx.") })?.Mid == "m1");

        Check("歌名对不上时返回 null",
            MatchScorer.Pick(new TrackInfo("完全不相干的名字", "某歌手", ""),
                new List<SongCandidate> { new("x", "夜曲", "周杰伦") }) is null);

        Check("搜索关键词优先只用歌名",
            MatchScorer.BuildSearchKeywords("雨是神的烟花 (我的爱不值一提吗)", "Nyxx.")[0] == "雨是神的烟花");
        Check("StripBrackets 处理全角括号", MatchScorer.StripBrackets("歌名（说明）") == "歌名");

        // 回归：真实踩过的坑。搜索 "Monster" 只会返回这些同名歌，歌手全对不上；
        // 如果只看"歌名全等"就定案，就会选中 BIGBANG 那首韩语歌。
        var monster = new List<SongCandidate>
        {
            new("k1", "Monsters", "Katie Sky"),
            new("k2", "Monster", "BIGBANG"),
            new("k3", "Monster", "Shawn Mendes/Justin Bieber"),
            new("k4", "Monster", "EXO"),
            new("k5", "Monster", "Alan Walker/Emyrson Flora"),
        };
        var monsterTrack = new TrackInfo("Monster", "Alan Walker/Emyrson Flora", "");
        Check("同名不同歌手时选歌手对得上的那条（Monster 回归）",
            MatchScorer.Pick(monsterTrack, monster)?.Mid == "k5",
            MatchScorer.Pick(monsterTrack, monster) is { } p ? $"{p.Name} - {p.Singer}" : "<null>");

        // 候选池里没有正确那条时，也说明为什么"只看第一个关键词"不可靠
        var withoutRightOne = monster.Where(c => c.Mid != "k5").ToList();
        Check("候选池缺正确曲目时会挑到同名但歌手不对的（因此必须多关键词比对）",
            MatchScorer.Pick(monsterTrack, withoutRightOne)?.Mid == "k2");

        Check("Evaluate 给出可用于比较各关键词的综合分",
            MatchScorer.Evaluate(monsterTrack, monster).Score >= 0.99,
            MatchScorer.Evaluate(monsterTrack, monster).Score.ToString("0.000"));

        // 回归：同名不同版本（英文版 / 日文版 / 中文版 / Remix）。
        // 搜索接口返回的名字全都不带版本标记，所以只看名字时前三条都是 1.000 —— 必须靠详情标题来区分。
        var daisy = new List<SongCandidate>
        {
            new("c1", "Daisy Crown", "鸣潮先约电台/Empty old City"),
            new("c2", "Daisy Crown", "Empty old City"),
            new("c3", "Daisy Crown", "鸣潮先约电台/芜桃/Empty old City/VISION SOUND"),
            new("c4", "Daisy Crown", "鸣潮先约电台/Tuno/ariiol"),
        };
        var daisyTrack = new TrackInfo("Daisy Crown (英文版)", "鸣潮先约电台/Empty old City", "");
        var rankedDaisy = MatchScorer.Rank(daisyTrack, daisy);
        Check("同名不同版本时仅靠名字无法区分（前几条并列满分，必须看详情标题）",
            rankedDaisy.Count >= 3 && rankedDaisy[0].Score >= 0.999 && rankedDaisy[2].Score >= 0.999,
            string.Join(" / ", rankedDaisy.Select(r => r.Candidate.Singer + "=" + r.Score.ToString("0.000"))));

        var trackQualifiers = MatchScorer.ExtractQualifiers(daisyTrack.Title);
        var japaneseQualifiers = MatchScorer.ExtractQualifiers("Daisy Crown (日文版)");
        var chineseQualifiers = MatchScorer.ExtractQualifiers("Daisy Crown (中文版)");
        var remixQualifiers = MatchScorer.ExtractQualifiers("Daisy Crown (Remix Ver.)");
        var plainQualifiers = MatchScorer.ExtractQualifiers("Daisy Crown");

        Check("能从标题括号里认出「英文版」",
            trackQualifiers.Contains(MatchScorer.QualifierKind.English), string.Join(",", trackQualifiers));
        Check("日文版与英文版冲突", MatchScorer.QualifiersConflict(trackQualifiers, japaneseQualifiers));
        Check("中文版与英文版冲突", MatchScorer.QualifiersConflict(trackQualifiers, chineseQualifiers));
        Check("Remix 与英文版冲突", MatchScorer.QualifiersConflict(trackQualifiers, remixQualifiers));
        Check("没有版本标记时不判冲突（信息不足，不能贸然排除）",
            !MatchScorer.QualifiersConflict(trackQualifiers, plainQualifiers));
        Check("曲目自己没有版本标记时不做任何限制",
            !MatchScorer.QualifiersConflict(new HashSet<MatchScorer.QualifierKind>(), japaneseQualifiers));
        Check("括号外的 live/english 不算版本标记（避免 Olivia / Englishman 这类误判）",
            MatchScorer.ExtractQualifiers("Olivia").Count == 0 &&
            MatchScorer.ExtractQualifiers("Englishman").Count == 0);
        Check("伴奏 / Live / 纯音乐 也能识别",
            MatchScorer.ExtractQualifiers("某歌 (伴奏)").Contains(MatchScorer.QualifierKind.Instrumental) &&
            MatchScorer.ExtractQualifiers("某歌 (Live)").Contains(MatchScorer.QualifierKind.Live) &&
            MatchScorer.ExtractQualifiers("某歌（纯音乐）").Contains(MatchScorer.QualifierKind.Instrumental));

        // 回归：全量搜索返回的曲名**带**版本标记，这时排序就该直接挑出英文版。
        var daisyFull = new List<SongCandidate>
        {
            new("e1", "Daisy Crown (日语)", "鸣潮先约电台/Empty old City"),
            new("e2", "Daisy Crown (英语)", "鸣潮先约电台/Empty old City"),
            new("e3", "Daisy Crown (国语)", "鸣潮先约电台/芜桃/Empty old City/VISION SOUND"),
            new("e4", "Daisy Crown (伴奏)", "鸣潮先约电台/Empty old City"),
        };
        var rankedFull = MatchScorer.Rank(daisyTrack, daisyFull);
        Check("曲名带版本标记时英文版排第一（Daisy Crown 回归）",
            rankedFull[0].Candidate.Mid == "e2" && rankedFull[0].Affinity == 1,
            rankedFull[0].Candidate.Name + "  affinity=" + rankedFull[0].Affinity +
            "  次优=" + rankedFull[1].Candidate.Name);
        Check("版本冲突的候选被排到最后（Affinity = -1）",
            rankedFull[rankedFull.Count - 1].Affinity == -1,
            rankedFull[rankedFull.Count - 1].Candidate.Name + "  affinity=" +
            rankedFull[rankedFull.Count - 1].Affinity);
    }

    // ---------------- 5. QQ 接口 ----------------
    private static async Task TestQqClientAsync()
    {
        Console.WriteLine();
        Console.WriteLine("---- 5. QQ 音乐接口 ----");

        using var client = new QQMusicClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var results = await client.SearchAsync("Prisoner Of Love", cts.Token);
        Check("搜索接口返回结果", results.Count > 0, $"{results.Count} 条");
        if (results.Count == 0) return;

        var detail = await client.GetDetailAsync(results[0].Mid, cts.Token);
        Check("歌曲详情返回 albumMid", detail is not null && detail.AlbumMid.Length > 0,
            detail is null ? "<null>" : $"albumMid={detail.AlbumMid}, 时长={detail.Duration.TotalSeconds}s");

        var lyric = await client.GetLyricAsync(results[0].Mid, cts.Token);
        Check("歌词接口返回内容", lyric is not null && lyric.Lyric.Length > 0,
            lyric is null ? "<null>" : $"{lyric.Lyric.Length} 字符");

        if (lyric is not null)
        {
            Check("真实歌词能解析出行", LrcParser.Parse(lyric.Lyric, lyric.Translation).Count > 0);
        }

        if (detail is not null && detail.AlbumMid.Length > 0)
        {
            byte[] cover = await client.DownloadAsync(
                QQMusicClient.BuildCoverUrl(detail.AlbumMid, 800), "https://y.qq.com/", cts.Token);
            bool isJpeg = cover is { Length: > 1000 } && cover[0] == 0xFF && cover[1] == 0xD8;
            Check("封面下载且是合法 JPEG", isJpeg, cover is null ? "<null>" : $"{cover.Length} 字节");
        }
    }

    // ---------------- 6. 实况匹配 ----------------
    private static async Task TestMatchAgainstLiveSearchAsync()
    {
        Console.WriteLine();
        Console.WriteLine("---- 6. 实况匹配（跑完整补全流程：搜索 → 打分 → 取歌词）----");

        TrackInfo track;
        if (_forcedTrack is not null)
        {
            track = _forcedTrack;
            Console.WriteLine("    （使用 --match 指定的曲目，不读 SMTC）");
        }
        else
        {
            using var media = new SmtcMediaService();
            await media.StartAsync();
            await Task.Delay(1200);
            track = media.Current;
        }

        Check("有可用的曲目", !track.IsEmpty, track.DisplayName);
        if (track.IsEmpty)
        {
            Console.WriteLine("    （QQ 音乐当前没有会话，跳过；请先播放一首歌，或用 --match \"歌名|歌手\" 指定）");
            return;
        }

        // 直接跑真实的补全器，而不是在测试里把搜索循环再抄一遍
        using var resolver = new LyricResolver(800);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        ResolvedLyrics resolved;
        try
        {
            resolved = await resolver.ResolveAsync(track, cts.Token);
        }
        catch (Exception ex)
        {
            Check("补全流程没有抛异常", false, ex.GetType().Name + ": " + ex.Message);
            return;
        }

        Check("能匹配到曲目", resolved.Found,
            resolved.Found ? $"{resolved.MatchedTitle} - {resolved.MatchedArtist}" : "未匹配");
        if (!resolved.Found) return;

        Check("能拿到带时间轴的歌词", resolved.Lines.Count > 0, $"{resolved.Lines.Count} 行");

        // 歌手是否对得上只作为提示打印：有些歌确实只有翻唱版本，不该判失败
        string originalFirst = MatchScorer.Normalize(track.Artist.Split('/')[0]);
        bool sameArtist = originalFirst.Length > 0 &&
            MatchScorer.Normalize(resolved.MatchedArtist).Contains(originalFirst, StringComparison.Ordinal);
        Console.WriteLine($"    （歌手核对：原曲 '{track.Artist}' vs 匹配 '{resolved.MatchedArtist}' → " +
                          (sameArtist ? "一致" : "不一致，可能是同名歌选错，或这首歌只有翻唱") + "）");
    }

    // ---------------- 7. SMTC 位置与外推 ----------------
    private static async Task TestSmtcAsync()
    {
        Console.WriteLine();
        Console.WriteLine("---- 7. SMTC 位置连续性与播放控制 ----");

        using var media = new SmtcMediaService();
        await media.StartAsync();
        await Task.Delay(1200);

        Check("检测到 QQ 音乐媒体会话", media.HasSession);
        if (!media.HasSession)
        {
            Check("无会话时 Current 为空", media.Current.IsEmpty);
            return;
        }

        Check("时长已知", media.Duration > TimeSpan.Zero, media.Duration.ToString());

        if (!media.IsPlaying)
        {
            // 暂停/停止时位置本来就不会推进，这里不该判失败
            Console.WriteLine("    （当前是暂停/停止状态，位置本就不推进，跳过连续性检查）");
        }
        else
        {
            TimeSpan p1 = media.CurrentPosition;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(3000);
            TimeSpan p2 = media.CurrentPosition;
            sw.Stop();

            double advanced = (p2 - p1).TotalSeconds;
            double wall = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"      位置 {p1:mm\\:ss\\.fff} -> {p2:mm\\:ss\\.fff}，真实经过 {wall:F2}s，推进 {advanced:F2}s");
            Check("位置按真实时间连续推进（误差 < 0.5s）", Math.Abs(advanced - wall) < 0.5,
                $"差值 {Math.Abs(advanced - wall):F3}s");
        }

        if (!_testPlayback)
        {
            Console.WriteLine("      播放控制测试已跳过（--test-playback 可启用）");
            return;
        }

        await media.TogglePlayPauseAsync();
        await Task.Delay(1500);
        bool pausedOk = !media.IsPlaying;

        await media.TogglePlayPauseAsync();
        await Task.Delay(1500);
        bool resumedOk = media.IsPlaying;

        Check("播放/暂停控制生效（已恢复原状态）", pausedOk && resumedOk,
            pausedOk ? (resumedOk ? "暂停与恢复都成功" : "暂停成功但恢复失败") : "暂停未生效");
    }

    // ---------------- 诊断：切换播放状态后退出 ----------------
    /// <summary>只做一件事：切换当前播放状态并退出。用来在做对照测量前把音乐恢复播放。</summary>
    private static async Task TogglePlaybackAsync()
    {
        using var media = new SmtcMediaService();
        await media.StartAsync();
        await Task.Delay(800);

        if (!media.HasSession)
        {
            Console.WriteLine("没有 QQ 音乐会话，无法切换播放状态");
            return;
        }

        Console.WriteLine("切换前: " + (media.IsPlaying ? "播放中" : "暂停/停止"));
        await media.TogglePlayPauseAsync();
        await Task.Delay(1500);
        Console.WriteLine("切换后: " + (media.IsPlaying ? "播放中" : "暂停/停止"));
    }
}
