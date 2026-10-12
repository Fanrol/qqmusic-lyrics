using System;

namespace QQMusicLyrics.Media;

/// <summary>
/// 判断一个 SMTC 会话是不是 QQ 音乐的。
///
/// 单独拆出来有两个原因：一是采集层的"只认 QQ 音乐"策略需要能被测试覆盖，
/// 二是浏览器播放 B 站视频时同样会注册媒体会话（SourceAppUserModelId 是 msedge.exe / chrome.exe），
/// 必须能可靠地把它们挡掉。
/// </summary>
public static class MediaSessionFilter
{
    /// <summary>目标可执行文件名。Win32 程序的 SourceAppUserModelId 就是可执行文件名（实测 QQ 音乐为 "QQMusic.exe"）。</summary>
    public const string TargetExecutable = "QQMusic.exe";

    public static bool IsQqMusic(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return false;

        string fileName = ExtractFileName(appUserModelId.Trim());
        return fileName.Equals(TargetExecutable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取出可执行文件名。SMTC 的 SourceAppUserModelId 有两种形态：
    /// 纯文件名（"QQMusic.exe"）或完整路径（"C:\...\QQMusic.exe"），
    /// 打包应用则是 "PackageFamilyName!AppId" 形式（这种一律不算）。
    /// </summary>
    private static string ExtractFileName(string appUserModelId)
    {
        int bang = appUserModelId.IndexOf('!');
        if (bang >= 0) return appUserModelId[(bang + 1)..];

        int separator = appUserModelId.LastIndexOfAny(new[] { '\\', '/' });
        return separator >= 0 ? appUserModelId[(separator + 1)..] : appUserModelId;
    }
}
