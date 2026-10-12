using System;
using System.IO;
using System.Text;

namespace QQMusicLyrics.Infra;

/// <summary>极简日志，带大小上限滚动，避免日志无限增长。</summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;

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
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string rolled = _path + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(_path, rolled);
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
