using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QQMusicLyrics.UI;

/// <summary>
/// 简约细线锁图标。菜单用位图（GDI+ 绘制），浮窗用 XAML 里的矢量 Path，
/// 两边共用同一套 24×24 设计坐标，保证观感一致。
///
/// 设计坐标（24×24）：
///   锁体   矩形 (5,11)-(19,21)，圆角半径 2.5
///   锁梁   从 (8.5,11) 竖直向上到 (8.5,7.5)，再以半径 3.5 画半圆到 (15.5,7.5)
///   关闭   半圆右侧再竖直落回 (15.5,11)
///   开启   右侧没有落回的一段，锁梁"抬起来"了
/// </summary>
internal static class LockGlyph
{
    /// <summary>生成菜单项用的锁图标。locked=true 画关着的锁。</summary>
    public static Bitmap CreateMenuBitmap(bool locked, Color color, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        float scale = size / 24f;
        using var pen = new Pen(color, 2.0f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        // 锁体
        using (var body = RoundedRectangle(5f * scale, 11f * scale, 14f * scale, 10f * scale, 2.5f * scale))
        {
            graphics.DrawPath(pen, body);
        }

        // 锁梁左腿
        graphics.DrawLine(pen, 8.5f * scale, 11f * scale, 8.5f * scale, 7.5f * scale);

        // 锁梁半圆（圆心 12,7.5 半径 3.5；从 180° 顺时针扫 180° 得到上半圆）
        graphics.DrawArc(pen,
            (12f - 3.5f) * scale, (7.5f - 3.5f) * scale, 7f * scale, 7f * scale,
            180f, 180f);

        // 关闭时右腿落回锁体
        if (locked)
        {
            graphics.DrawLine(pen, 15.5f * scale, 7.5f * scale, 15.5f * scale, 11f * scale);
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
