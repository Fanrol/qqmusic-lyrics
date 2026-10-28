using System;
using System.IO;
using System.Text.Json;
using QQMusicLyrics.Infra;

namespace QQMusicLyrics.Settings;

/// <summary>用户可调项。持久化到 %APPDATA%\QQMusicLyrics\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>
    /// 设置结构版本。变动默认值或删除字段时递增，并在 <see cref="Migrate"/> 里写清迁移规则；
    /// 否则老用户的 settings.json 会一直沿用旧值（例如窗口尺寸永远停在 1000x180）。
    /// </summary>
    public int SettingsVersion { get; set; }

    private const int CurrentVersion = 3;

    public const double DefaultWidth = 480;
    public const double DefaultHeight = 116;
    public const double DefaultFontSize = 30;

    // ---- 窗口位置（null 表示"尚未确定"，首次运行会摆到屏幕底部居中）----
    // 刻意用可空 double 而不是 NaN：System.Text.Json 默认无法把 NaN 写成合法 JSON，
    // 会让"位置还没确定时的保存"直接抛异常并静默失败。
    public double? Left { get; set; }
    public double? Top { get; set; }

    /// <summary>单位是 WPF 的 DIP（设备无关像素），不是物理像素。</summary>
    public double Width { get; set; } = DefaultWidth;

    public double Height { get; set; } = DefaultHeight;

    // ---- 外观 ----
    public double FontSize { get; set; } = DefaultFontSize;
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>
    /// 锁定状态。锁定 = 同时开启鼠标穿透（鼠标事件直接穿过浮窗），解锁 = 取消穿透。
    /// 这是唯一的穿透开关，不再单独提供 ClickThrough 设置。
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>是否显示翻译。单行浮窗默认不显示。</summary>
    public bool ShowTranslation { get; set; }

    /// <summary>QQ 音乐没有媒体会话时完全不显示浮窗（例如只有浏览器在放 B 站视频）。</summary>
    public bool HideWhenNoQqMusic { get; set; } = true;

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
                    Migrate(loaded);
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，改用默认值", ex);
        }

        return new AppSettings { SettingsVersion = CurrentVersion };
    }

    /// <summary>按版本补齐或重置字段；老文件里已删除的字段（如 ClickThrough / ShowBackdrop）会被忽略。</summary>
    private static void Migrate(AppSettings settings)
    {
        int from = settings.SettingsVersion;

        if (from < 2)
        {
            // v2：浮窗从 1000x180 的三行布局改为 480x116 的单行布局
            settings.Width = DefaultWidth;
            settings.Height = DefaultHeight;
            settings.FontSize = DefaultFontSize;
            settings.ShowTranslation = false;
            settings.Left = null;
            settings.Top = null;
        }

        if (from < 3)
        {
            // v3：锁定与鼠标穿透合并成一个概念，背景板整体移除。
            // 旧值语义不同，统一重置为"未锁定"，保证一启动就能拖动窗口。
            settings.Locked = false;
        }

        if (from < CurrentVersion)
        {
            Log.Info($"设置已从 v{from} 迁移到 v{CurrentVersion}");
        }

        settings.SettingsVersion = CurrentVersion;
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

    /// <summary>把明显越界的值拉回合理范围，避免手改 settings.json 之后窗口消失或看不见。</summary>
    public void Sanitize()
    {
        Width = Clamp(Width, 200, 8000, DefaultWidth);
        Height = Clamp(Height, 60, 2000, DefaultHeight);
        FontSize = Clamp(FontSize, 12, 120, DefaultFontSize);
        WindowOpacity = Clamp(WindowOpacity, 0.15, 1.0, 1.0);
        CoverSize = CoverSize is 300 or 500 or 800 ? CoverSize : 800;
        LyricOffsetMs = (int)Clamp(LyricOffsetMs, -10000, 10000, 0);
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
        return Math.Min(max, Math.Max(min, value));
    }
}
