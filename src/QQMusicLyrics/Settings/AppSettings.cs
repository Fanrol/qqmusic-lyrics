using System;
using System.IO;
using System.Text.Json;
using QQMusicLyrics.Infra;

namespace QQMusicLyrics.Settings;

/// <summary>用户可调项。持久化到 %APPDATA%\QQMusicLyrics\settings.json。</summary>
public sealed class AppSettings
{
    // ---- 窗口位置与尺寸（NaN 表示"尚未确定"，首次运行会摆到屏幕底部居中）----
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 180;

    // ---- 外观 ----
    public double FontSize { get; set; } = 34;
    public double NeighborFontScale { get; set; } = 0.60;
    public double WindowOpacity { get; set; } = 1.0;
    public bool ShowBackdrop { get; set; } = true;
    public double BackdropOpacity { get; set; } = 0.28;
    public bool ShowNeighborLines { get; set; } = true;

    // ---- 行为 ----
    /// <summary>鼠标穿透：开启后鼠标事件直接穿过浮窗，打游戏时不会挡住操作。默认关闭以便首次运行能拖动。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>锁定位置：禁止拖动。</summary>
    public bool Locked { get; set; }

    public bool ShowTranslation { get; set; } = true;
    public bool HideWhenIdle { get; set; } = true;
    public bool AutoStart { get; set; }

    /// <summary>歌词整体偏移（毫秒），正数表示歌词提前。</summary>
    public int LyricOffsetMs { get; set; }

    /// <summary>封面请求的分辨率档位（300/500/800）。</summary>
    public int CoverSize { get; set; } = 800;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，改用默认值", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
        }
    }

    /// <summary>把明显越界的值拉回合理范围，避免手改 settings.json 之后窗口消失。</summary>
    public void Sanitize()
    {
        Width = Clamp(Width, 320, 8000, 1000);
        Height = Clamp(Height, 90, 2000, 180);
        FontSize = Clamp(FontSize, 14, 120, 34);
        NeighborFontScale = Clamp(NeighborFontScale, 0.3, 1.0, 0.60);
        WindowOpacity = Clamp(WindowOpacity, 0.15, 1.0, 1.0);
        BackdropOpacity = Clamp(BackdropOpacity, 0.0, 1.0, 0.28);
        CoverSize = CoverSize is 300 or 500 or 800 ? CoverSize : 800;
        LyricOffsetMs = (int)Clamp(LyricOffsetMs, -10000, 10000, 0);
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
        return Math.Min(max, Math.Max(min, value));
    }
}
