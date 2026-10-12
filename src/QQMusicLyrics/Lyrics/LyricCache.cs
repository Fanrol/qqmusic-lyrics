using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QQMusicLyrics.Infra;
using QQMusicLyrics.Models;

namespace QQMusicLyrics.Lyrics;

/// <summary>歌词缓存条目。存的是原始 LRC 文本而不是解析结果，这样以后换解析器不用让缓存失效。</summary>
public sealed class LyricCacheEntry
{
    public string SongMid { get; set; } = string.Empty;
    public string MatchedTitle { get; set; } = string.Empty;
    public string MatchedArtist { get; set; } = string.Empty;
    public string AlbumName { get; set; } = string.Empty;
    public string AlbumMid { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string Lyric { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 磁盘缓存。作用有两个：省掉重复的网络请求；以及避免"歌名 → 歌曲 ID"每次都要重新匹配
/// （匹配是整条链路里唯一不精确的环节，一旦匹配对了一次就应该固定下来）。
/// </summary>
public sealed class LyricCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public LyricCacheEntry? TryGet(TrackInfo track)
    {
        try
        {
            string path = PathFor(track);
            if (!File.Exists(path)) return null;

            var entry = JsonSerializer.Deserialize<LyricCacheEntry>(File.ReadAllText(path), Options);
            if (entry is null) return null;

            if (DateTimeOffset.Now - entry.SavedAt > MaxAge)
            {
                Log.Info("歌词缓存已过期，重新匹配: " + track.DisplayName);
                return null;
            }
            return entry;
        }
        catch (Exception ex)
        {
            Log.Warn("读取歌词缓存失败: " + ex.Message);
            return null;
        }
    }

    public void Save(TrackInfo track, LyricCacheEntry entry)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(PathFor(track), JsonSerializer.Serialize(entry, Options));
        }
        catch (Exception ex)
        {
            Log.Warn("写入歌词缓存失败: " + ex.Message);
        }
    }

    public void Invalidate(TrackInfo track)
    {
        try
        {
            string path = PathFor(track);
            if (File.Exists(path)) File.Delete(path);
            Log.Info("已清除该曲目的歌词缓存: " + track.DisplayName);
        }
        catch (Exception ex)
        {
            Log.Warn("清除歌词缓存失败: " + ex.Message);
        }
    }

    private static string PathFor(TrackInfo track)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(track.CacheKey));
        string name = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(AppPaths.LyricCacheDir, name + ".json");
    }
}
