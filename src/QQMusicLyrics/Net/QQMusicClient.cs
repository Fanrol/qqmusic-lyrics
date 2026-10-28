using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Models;

namespace QQMusicLyrics.Net;

/// <summary>
/// QQ 音乐 Web 接口客户端。
///
/// 只使用公开的只读接口（搜索 / 歌曲详情 / 歌词 / 封面图），不触碰任何账号数据，
/// 也不需要登录凭证。注意两个实测要点：
///   1. 这些接口对 Referer 有校验，缺失会直接返回 500；
///   2. 搜索走**全量搜索** `soso/fcgi-bin/search_for_qq_cp`（一次 30 条，songname 里带版本标记），
///      取不到时退回联想接口 `smartbox_new.fcg`（只回 4 条左右，常把同名不同版本的那些漏掉）。
/// </summary>
public sealed class QQMusicClient : IDisposable
{
    private static readonly Regex Base64Like = new(@"^[A-Za-z0-9+/=\s]+$", RegexOptions.Compiled);
    private static readonly Regex HasTimeTag = new(@"\[\d{1,3}:\d{1,2}", RegexOptions.Compiled);

    /// <summary>全量搜索一次取多少条。</summary>
    private const int FullSearchLimit = 30;

    private readonly HttpClient _http;

    public QQMusicClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,

            // 实测坑：y.gtimg.cn 解析出 9 个地址，其中偶尔有一个 TCP 连接会永久黑洞（连 SYN 都不回）。
            // SocketsHttpHandler 的 ConnectTimeout 默认是"无限"，于是整条请求会一直卡到
            // HttpClient 的 15 秒整体超时，期间不会换用其它地址。
            // 把它压到 5 秒，坏地址会被及时放弃、改用下一个可用地址。
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    /// <summary>搜索接口接受的来源页。</summary>
    private const string SearchReferer = "https://y.qq.com/";

    /// <summary>歌词接口实测需要播放器页作为来源，用首页会拿不到内容。</summary>
    private const string LyricReferer = "https://y.qq.com/portal/player.html";

    /// <summary>
    /// 按关键词搜索候选曲目。
    ///
    /// 优先用**全量搜索**：一次能拿 30 条，而且 `songname` 里带着版本标记
    /// （"(英语)"、"(日语)"、"(国语)"、"(伴奏)"、"Remix"…），这对区分同名不同版本是决定性的。
    /// 联想接口 smartbox 只回 4 条左右，实测播放 "Daisy Crown (英文版)" 时它把英文版整条漏掉了，
    /// 导致只能在日文版/中文版里挑。所以只有全量搜索拿不到东西时才退回联想接口。
    /// </summary>
    public async Task<IReadOnlyList<SongCandidate>> SearchAsync(string keyword, CancellationToken token)
    {
        var full = await SearchFullAsync(keyword, token).ConfigureAwait(false);
        if (full.Count > 0) return full;
        return await SuggestAsync(keyword, token).ConfigureAwait(false);
    }

    /// <summary>全量搜索（老 PC 客户端接口）：返回条数多，且曲名里带版本标记。</summary>
    private async Task<List<SongCandidate>> SearchFullAsync(string keyword, CancellationToken token)
    {
        var results = new List<SongCandidate>();
        if (string.IsNullOrWhiteSpace(keyword)) return results;

        string url = "https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp?w="
                     + Uri.EscapeDataString(keyword)
                     + "&format=json&n=" + FullSearchLimit
                     + "&p=1&aggr=1&cr=1&lossless=0&new_json=1";

        string? json = await GetStringAsync(url, SearchReferer, token).ConfigureAwait(false);
        if (json is null) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return results;
            if (!data.TryGetProperty("song", out var song)) return results;
            if (!song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return results;

            foreach (var item in list.EnumerateArray())
            {
                string mid = GetString(item, "songmid");
                if (mid.Length == 0) continue;

                var singers = new List<string>();
                if (item.TryGetProperty("singer", out var singer) && singer.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in singer.EnumerateArray()) singers.Add(GetString(s, "name"));
                }

                string name = GetString(item, "songname");
                if (name.Length == 0) name = GetString(item, "title");

                results.Add(new SongCandidate(mid, name, string.Join("/", singers)));
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"解析全量搜索结果失败: {ex.Message}");
        }

        return results;
    }

    /// <summary>联想接口（兜底）：只用于全量搜索失效时，条数少且不带版本标记。</summary>
    private async Task<List<SongCandidate>> SuggestAsync(string keyword, CancellationToken token)
    {
        var results = new List<SongCandidate>();
        if (string.IsNullOrWhiteSpace(keyword)) return results;

        string url = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?key="
                     + Uri.EscapeDataString(keyword)
                     + "&format=json&inCharset=utf8&outCharset=utf-8";

        string? json = await GetStringAsync(url, SearchReferer, token).ConfigureAwait(false);
        if (json is null) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return results;
            if (!data.TryGetProperty("song", out var song)) return results;
            if (!song.TryGetProperty("itemlist", out var itemList) || itemList.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in itemList.EnumerateArray())
            {
                string mid = GetString(item, "mid");
                if (mid.Length == 0) continue;
                results.Add(new SongCandidate(mid, GetString(item, "name"), GetString(item, "singer")));
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"解析搜索结果失败: {ex.Message}");
        }

        return results;
    }

    /// <summary>用 songmid 换歌曲详情（主要是为了拿到 albumMid 用于取封面）。</summary>
    public async Task<SongDetail?> GetDetailAsync(string songMid, CancellationToken token)
    {
        string url = "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg?songmid="
                     + Uri.EscapeDataString(songMid)
                     + "&platform=yqq&format=json&inCharset=utf8&outCharset=utf-8";

        string? json = await GetStringAsync(url, SearchReferer, token).ConfigureAwait(false);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                return null;
            }

            var track = data[0];
            string albumMid = string.Empty;
            string albumName = string.Empty;
            if (track.TryGetProperty("album", out var album))
            {
                albumMid = GetString(album, "mid");
                albumName = GetString(album, "name");
            }

            var singers = new List<string>();
            if (track.TryGetProperty("singer", out var singer) && singer.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in singer.EnumerateArray()) singers.Add(GetString(s, "name"));
            }

            int interval = 0;
            if (track.TryGetProperty("interval", out var intervalElement) &&
                intervalElement.TryGetInt32(out int intervalValue))
            {
                interval = intervalValue;
            }

            return new SongDetail(
                songMid,
                GetString(track, "title"),
                string.Join("/", singers),
                albumMid,
                albumName,
                TimeSpan.FromSeconds(interval));
        }
        catch (JsonException ex)
        {
            Log.Warn($"解析歌曲详情失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>取歌词（含可选翻译）。</summary>
    public async Task<LyricPayload?> GetLyricAsync(string songMid, CancellationToken token)
    {
        string url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid="
                     + Uri.EscapeDataString(songMid)
                     + "&format=json&nobase64=1&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8"
                     + "&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";

        string? json = await GetStringAsync(url, LyricReferer, token).ConfigureAwait(false);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string lyric = GetString(root, "lyric");
            string translation = GetString(root, "trans");

            // 少数歌曲即使带了 nobase64=1 依然返回 base64，这里兜一下
            if (lyric.Length > 0 && !HasTimeTag.IsMatch(lyric) && Base64Like.IsMatch(lyric) && lyric.Length > 40)
            {
                string? decoded = TryDecodeBase64(lyric);
                if (decoded is not null) lyric = decoded;
            }
            if (translation.Length > 0 && !HasTimeTag.IsMatch(translation) &&
                Base64Like.IsMatch(translation) && translation.Length > 40)
            {
                string? decoded = TryDecodeBase64(translation);
                if (decoded is not null) translation = decoded;
            }

            if (lyric.Length == 0) return null;
            return new LyricPayload(lyric, translation);
        }
        catch (JsonException ex)
        {
            Log.Warn($"解析歌词失败: {ex.Message}");
            return null;
        }
    }

    public static string BuildCoverUrl(string albumMid, int size) =>
        $"https://y.gtimg.cn/music/photo_new/T002R{size}x{size}M000{albumMid}.jpg";

    /// <summary>下载二进制（封面图）。失败会重试一次——坏地址是概率事件，重试通常就换到好地址了。</summary>
    public async Task<byte[]?> DownloadAsync(string url, string referer, CancellationToken token)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            token.ThrowIfCancellationRequested();

            byte[]? result = await DownloadOnceAsync(url, referer, token).ConfigureAwait(false);
            if (result is not null) return result;

            if (attempt < 2)
            {
                Log.Info("下载失败，重试一次: " + url);
                await Task.Delay(300, token).ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task<byte[]?> DownloadOnceAsync(string url, string referer, CancellationToken token)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(referer);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"下载失败 HTTP {(int)response.StatusCode}: {url}");
                return null;
            }
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            Log.Info($"下载完成 {bytes.Length} 字节 / {stopwatch.ElapsedMilliseconds}ms: {url}");
            return bytes;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw; // 真的是调用方取消了（例如已经切歌）
        }
        catch (Exception ex)
        {
            // 注意：HttpClient 自身超时抛的也是 OperationCanceledException，但此时 token 并没有被取消。
            // 早期版本在这里直接 rethrow，导致超时被上层误判成"用户取消"，整首歌的歌词被静默丢弃。
            Log.Warn($"下载异常({stopwatch.ElapsedMilliseconds}ms): {ex.GetType().Name}: {ex.Message} <- {url}");
            return null;
        }
    }

    private async Task<string?> GetStringAsync(string url, string referer, CancellationToken token)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri(referer);
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"HTTP {(int)response.StatusCode} <- {url}");
                    if (attempt == 2) return null;
                    await Task.Delay(400, token).ConfigureAwait(false);
                    continue;
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // 调用方主动取消
            }
            catch (Exception ex)
            {
                Log.Warn($"请求失败({attempt}/2): {ex.GetType().Name}: {ex.Message} <- {url}");
                if (attempt == 2) return null;
                await Task.Delay(400, token).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static string? TryDecodeBase64(string value)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(value.Trim());
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty,
        };
    }

    public void Dispose() => _http.Dispose();
}
