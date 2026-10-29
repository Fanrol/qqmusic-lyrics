using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using QQMusicLyrics.Interop;
using QQMusicLyrics.Models;

namespace QQMusicLyrics.UI;

/// <summary>
/// 托盘图标与右键菜单。
///
/// 浮窗常驻置顶，锁定后还会整窗鼠标穿透，所以托盘是"永远够得着"的控制入口；
/// 托盘菜单与浮窗右键菜单是同一个对象，保证两处完全一致。
///
/// 菜单：显示 / 隐藏 / 锁定(解锁) / 字号 + / 字号 − / 退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>菜单项上下留白，让行距松一点。</summary>
    private static readonly Padding ItemPadding = new(8, 7, 8, 7);

    /// <summary>菜单图标的绘制颜色（菜单是浅色底）。</summary>
    private static readonly Color MenuGlyphColor = Color.FromArgb(60, 60, 60);

    private readonly NotifyIcon _notifyIcon;
    private readonly StickyContextMenu _menu;
    private readonly AppCommands _commands;
    private readonly Icon _icon;

    private readonly ToolStripMenuItem _showItem = new("显示");
    private readonly ToolStripMenuItem _hideItem = new("隐藏");
    private readonly ToolStripMenuItem _lockItem = new("锁定");
    private readonly ToolStripMenuItem _fontPlusItem = new("字号 +");
    private readonly ToolStripMenuItem _fontMinusItem = new("字号 −");
    private readonly ToolStripMenuItem _settingsItem = new("设置");
    private readonly ToolStripMenuItem _exitItem = new("退出");

    private readonly Bitmap _lockOpenImage;
    private readonly Bitmap _lockClosedImage;
    private readonly Bitmap _plusImage;
    private readonly Bitmap _minusImage;
    private readonly Bitmap _gearImage;

    public TrayIcon(AppCommands commands)
    {
        _commands = commands;
        _icon = CreateIcon();

        _lockOpenImage = Glyphs.CreateLockBitmap(locked: false, MenuGlyphColor, 32);
        _lockClosedImage = Glyphs.CreateLockBitmap(locked: true, MenuGlyphColor, 32);
        _plusImage = Glyphs.CreatePlusBitmap(MenuGlyphColor, 32);
        _minusImage = Glyphs.CreateMinusBitmap(MenuGlyphColor, 32);
        _gearImage = Glyphs.CreateGearBitmap(MenuGlyphColor, 32);

        _menu = new StickyContextMenu
        {
            ShowImageMargin = true, // 给左侧图标留出位置
            Font = new Font("Microsoft YaHei UI", 9f),
            ImageScalingSize = new Size(18, 18),
        };
        BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "QQ音乐浮窗歌词",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _commands.StateChanged += (_, _) => SyncMenuState();
        SyncMenuState();
    }

    /// <summary>
    /// 点「字号 +」「字号 −」之后菜单**不关闭**，方便连续点；其余菜单项照旧，点完就关。
    /// 判定依据是"点击位置落在带 StickyTag 的项上"，比在 Click 里设标志更可靠
    /// （实测 ToolStrip 的 Closing 早于 Click，用标志会来不及）。
    /// </summary>
    private sealed class StickyContextMenu : ContextMenuStrip
    {
        public const string StickyTag = "sticky";

        protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && IsOverStickyItem())
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        private bool IsOverStickyItem()
        {
            Point cursor = PointToClient(Cursor.Position);
            foreach (ToolStripItem item in Items)
            {
                if (item is ToolStripMenuItem menuItem &&
                    (menuItem.Tag as string) == StickyTag &&
                    menuItem.Bounds.Contains(cursor))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 让菜单项的文字在整行里垂直居中。
    /// 默认实现会把文字贴在内容区顶部，实测比行中心高约 5.7px（图标却是居中的），
    /// 所以这里直接把文字的绘制矩形挪到行的垂直中心。
    /// </summary>
    private sealed class CenteredMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is ToolStripMenuItem)
            {
                Rectangle bounds = e.Item.Bounds;
                Size textSize = TextRenderer.MeasureText(e.Text ?? string.Empty, e.TextFont);

                Rectangle rect = e.TextRectangle;
                int top = bounds.Top + (bounds.Height - textSize.Height) / 2;
                e.TextRectangle = new Rectangle(rect.X, top, rect.Width, textSize.Height);
                e.TextFormat |= TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            }

            base.OnRenderItemText(e);
        }
    }

    private void BuildMenu()
    {
        // 固定尺寸 + MiddleLeft：保证文字与左侧图标在同一水平中线上
        foreach (ToolStripMenuItem item in new[]
                 {
                     _showItem, _hideItem, _lockItem, _fontPlusItem,
                     _fontMinusItem, _settingsItem, _exitItem,
                 })
        {
            item.AutoSize = false;
            item.Width = 200;
            // 行高刻意贴近"文字高度 + 上下留白"：默认实现把文字按内容区顶部对齐，
            // 行高多出来的部分全落在文字下面，看起来就偏上了（实测差约 5.7px）。
            // 把高度收到 29 之后，内容区高度≈文字高度，文字自然就居中、和图标对齐了。
            item.Height = 29;
            item.TextAlign = ContentAlignment.MiddleLeft;
            item.ImageAlign = ContentAlignment.MiddleLeft;
            item.Padding = ItemPadding;
        }

        // 字号项：点完保留菜单（靠 Tag 让菜单自己判断）
        _showItem.Click += (_, _) => _commands.ShowWindow();
        _hideItem.Click += (_, _) => _commands.HideWindow();
        _lockItem.Click += (_, _) => _commands.ToggleLocked();
        _fontPlusItem.Tag = StickyContextMenu.StickyTag;
        _fontMinusItem.Tag = StickyContextMenu.StickyTag;
        _fontPlusItem.Click += (_, _) => _commands.IncreaseFont();
        _fontMinusItem.Click += (_, _) => _commands.DecreaseFont();
        _settingsItem.Click += (_, _) => _commands.OpenSettings();
        _exitItem.Click += (_, _) => _commands.Exit();

        _fontPlusItem.Image = _plusImage;
        _fontMinusItem.Image = _minusImage;
        _settingsItem.Image = _gearImage;

        _menu.Items.Add(_showItem);
        _menu.Items.Add(_hideItem);
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(_fontPlusItem);
        _menu.Items.Add(_fontMinusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_exitItem);

        // 每次弹出前刷新锁定项的文案与图标
        _menu.Opening += (_, _) => SyncMenuState();
    }

    /// <summary>
    /// 锁定项显示的是"下一步动作"：未锁定时写「锁定」并配开着的锁；
    /// 锁定后写「解锁」并配关着的锁。
    /// </summary>
    private void SyncMenuState()
    {
        bool locked = _commands.IsLocked;
        _lockItem.Text = locked ? "解锁" : "锁定";
        _lockItem.Image = locked ? _lockClosedImage : _lockOpenImage;
    }

    /// <summary>在鼠标位置弹出菜单（浮窗右键时复用同一个菜单）。</summary>
    public void ShowMenu()
    {
        SyncMenuState();
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
        _lockOpenImage.Dispose();
        _lockClosedImage.Dispose();
        _plusImage.Dispose();
        _minusImage.Dispose();
        _gearImage.Dispose();
    }
}
