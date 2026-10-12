using System;
using Microsoft.Win32;
using QQMusicLyrics.Infra;

namespace QQMusicLyrics.Interop;

/// <summary>
/// 开机自启（可选功能）。只动 HKCU 下 Run 键，不碰任何系统级位置，禁用时把键值删掉。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QQMusicLyrics";

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("读取开机自启状态失败: " + ex.Message);
            return false;
        }
    }

    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                string exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe))
                {
                    Log.Warn("无法确定可执行文件路径，跳过开机自启设置");
                    return false;
                }
                key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Log.Info($"开机自启已{(enabled ? "开启" : "关闭")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            return false;
        }
    }
}
