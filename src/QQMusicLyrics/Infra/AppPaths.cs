using System;
using System.IO;

namespace QQMusicLyrics.Infra;

/// <summary>应用运行期文件的统一位置。全部集中在 %APPDATA%\QQMusicLyrics 下，不污染其他目录。</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QQMusicLyrics");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "log.txt");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string CoverDir => Path.Combine(CacheDir, "covers");
    public static string LyricCacheDir => Path.Combine(CacheDir, "lyrics");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(CoverDir);
        Directory.CreateDirectory(LyricCacheDir);
    }
}
