using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using QQMusicLyrics.Interop;
using QQMusicLyrics.Models;

namespace QQMusicLyrics.UI;

/// <summary>
/// 托盘图标与菜单。因为浮窗常驻置顶且可能开启鼠标穿透，托盘是唯一"永远够得着"的控制入口。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly AppCommands _commands;
    private readonly Icon _icon;

    private readonly ToolStripMenuItem _visibleItem = new("显示浮窗");
    private readonly ToolStripMenuItem _clickThroughItem = new("鼠标穿透（打游戏不挡操作）");
    private readonly ToolStripMenuItem _lockedItem = new("锁定位置");
    private readonly ToolStripMenuItem _hideWhenIdleItem = new("无播放时自动隐藏");

    private readonly ToolStripMenuItem _translationItem = new("显示翻译歌词");
    private readonly ToolStripMenuItem _neighborItem = new("显示上下行歌词");
    private readonly ToolStripMenuItem _backdropItem = new("显示深色背景板");
    private readonly ToolStripMenuItem _offsetItem = new("歌词偏移：0 ms");
    private readonly ToolStripMenuItem _fontItem = new("字号：34");

    private readonly ToolStripMenuItem _autoStartItem = new("开机自启");

    public TrayIcon(AppCommands commands)
    {
        _commands = commands;
        _icon = CreateIcon();

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "QQ音乐浮窗歌词",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => _commands.ToggleVisible();

        _commands.StateChanged += (_, _) => SyncCheckedState();
        SyncCheckedState();
    }

    private void BuildMenu()
    {
        _visibleItem.Click += (_, _) => _commands.ToggleVisible();
        _clickThroughItem.Click += (_, _) => _commands.ToggleClickThrough();
        _lockedItem.Click += (_, _) => _commands.ToggleLocked();
        _hideWhenIdleItem.Click += (_, _) => _commands.ToggleHideWhenIdle();

        _menu.Items.Add(_visibleItem);
        _menu.Items.Add(_clickThroughItem);
        _menu.Items.Add(_lockedItem);
        _menu.Items.Add(_hideWhenIdleItem);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(MakeItem("上一首", () => _commands.PreviousTrack()));
        _menu.Items.Add(MakeItem("播放 / 暂停", () => _commands.PlayPause()));
        _menu.Items.Add(MakeItem("下一首", () => _commands.NextTrack()));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(MakeItem("重新匹配歌词", () => _commands.ReloadLyrics()));
        _menu.Items.Add(new ToolStripSeparator());

        _translationItem.Click += (_, _) => _commands.ToggleTranslation();
        _neighborItem.Click += (_, _) => _commands.ToggleNeighborLines();
        _backdropItem.Click += (_, _) => _commands.ToggleBackdrop();
        _menu.Items.Add(_translationItem);
        _menu.Items.Add(_neighborItem);
        _menu.Items.Add(_backdropItem);

        var offsetEarlier = MakeItem("歌词提前 100 ms", () => _commands.ShiftLyrics(+100));
        var offsetLater = MakeItem("歌词延后 100 ms", () => _commands.ShiftLyrics(-100));
        var offsetReset = MakeItem("偏移归零", () => _commands.ResetLyricOffset());
        _offsetItem.Enabled = false;
        _menu.Items.Add(_offsetItem);
        _menu.Items.Add(offsetEarlier);
        _menu.Items.Add(offsetLater);
        _menu.Items.Add(offsetReset);

        var fontPlus = MakeItem("字号 +", () => _commands.IncreaseFont());
        var fontMinus = MakeItem("字号 −", () => _commands.DecreaseFont());
        var fontReset = MakeItem("字号重置", () => _commands.ResetFont());
        _fontItem.Enabled = false;
        _menu.Items.Add(_fontItem);
        _menu.Items.Add(fontPlus);
        _menu.Items.Add(fontMinus);
        _menu.Items.Add(fontReset);
        _menu.Items.Add(new ToolStripSeparator());

        _autoStartItem.Click += (_, _) => _commands.ToggleAutoStart();
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(MakeItem("退出", () => _commands.Exit()));
    }

    private static ToolStripMenuItem MakeItem(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>菜单每次弹出前同步勾选状态，避免显示的是旧状态。</summary>
    private void SyncCheckedState()
    {
        _visibleItem.Checked = _commands.IsVisible;
        _clickThroughItem.Checked = _commands.Settings.ClickThrough;
        _lockedItem.Checked = _commands.Settings.Locked;
        _hideWhenIdleItem.Checked = _commands.Settings.HideWhenIdle;
        _translationItem.Checked = _commands.Settings.ShowTranslation;
        _neighborItem.Checked = _commands.Settings.ShowNeighborLines;
        _backdropItem.Checked = _commands.Settings.ShowBackdrop;
        _offsetItem.Text = $"歌词偏移：{_commands.Settings.LyricOffsetMs} ms";
        _fontItem.Text = $"字号：{_commands.Settings.FontSize:0}";
        _autoStartItem.Checked = _commands.IsAutoStartEnabled;
    }

    /// <summary>在鼠标位置弹出菜单（浮窗右键时复用同一个菜单）。</summary>
    public void ShowMenu()
    {
        SyncCheckedState();
        _menu.Show(System.Windows.Forms.Cursor.Position);
    }

    public void UpdateTrackInfo(TrackInfo track, ResolvedLyrics? lyrics)
    {
        string text;
        if (track.IsEmpty)
        {
            text = "QQ音乐浮窗歌词 · 未在播放";
        }
        else if (lyrics is { Found: true })
        {
            text = $"{track.DisplayName} · 歌词：{lyrics.MatchedTitle} - {lyrics.MatchedArtist}";
        }
        else
        {
            text = $"{track.DisplayName} · 未匹配到歌词";
        }

        // NotifyIcon.Text 有 63 字符上限，超了会直接抛异常
        if (text.Length > 63) text = text.Substring(0, 62) + "…";
        _notifyIcon.Text = text;
    }

    /// <summary>运行时画一个图标，省掉一个二进制资源文件。</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 31, 190, 116)); // QQ 绿
            graphics.FillEllipse(background, 1, 1, 30, 30);

            using var font = new Font("Segoe UI Symbol", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            graphics.DrawString("♪", font, Brushes.White, new RectangleF(1, 2, 30, 30), format);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // FromHandle 不接管句柄所有权，必须 Clone 之后自己销毁句柄
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIconHandle(handle);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
