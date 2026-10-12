using System;
using System.IO;
using System.Text;

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

/// <summary>极简日志，带大小上限滚动，避免日志无限增长。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _path = AppPaths.LogFile;

    public static void Init(string path) => _path = path;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    string old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志失败绝不能影响主流程
            }
        }
    }
}
