using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QQMusicLyrics.UI;

/// <summary>
/// 浮窗上的简约细线图标：锁 / 加号 / 减号。
/// 菜单用 GDI+ 位图绘制，浮窗用 XAML 矢量 Path，两边共用同一套 24×24 设计坐标，保证观感一致。
///
/// 设计坐标（24×24）：
///   锁体   矩形 (5,11)-(19,21)，圆角半径 2.5
///   锁梁   从 (8.5,11) 竖直向上到 (8.5,7.5)，再以半径 3.5 从 180° 顺时针扫过
///          关闭：扫 180° 到 (15.5,7.5)，右腿再落回 (15.5,11)
///          开启：只扫 130° 到 (14.25,4.82)，右侧留出明显的缺口（小尺寸下也看得出是开着的）
///   加减   中心 (12,13)，横线 (6,13)-(18,13)；加号另有竖线 (12,7)-(12,19)
/// </summary>
internal static class Glyphs
{
    /// <summary>菜单图标：锁。locked=true 画关着的锁。</summary>
    public static Bitmap CreateLockBitmap(bool locked, Color color, int size)
    {
        var bitmap = NewBitmap(size);
        using var graphics = Graphics.FromImage(bitmap);
        Prepare(graphics);
        using var pen = NewPen(color, size);

        float scale = size / 24f;

        using (var body = RoundedRectangle(5f * scale, 11f * scale, 14f * scale, 10f * scale, 2.5f * scale))
        {
            graphics.DrawPath(pen, body);
        }

        // 锁梁左腿
        graphics.DrawLine(pen, 8.5f * scale, 11f * scale, 8.5f * scale, 7.5f * scale);

        // 锁梁半圆：关闭扫 180°，开启只扫 130°（留大缺口）
        float sweep = locked ? 180f : 130f;
        graphics.DrawArc(pen,
            (12f - 3.5f) * scale, (7.5f - 3.5f) * scale, 7f * scale, 7f * scale,
            180f, sweep);

        if (locked)
        {
            graphics.DrawLine(pen, 15.5f * scale, 7.5f * scale, 15.5f * scale, 11f * scale);
        }

        return bitmap;
    }

    /// <summary>菜单图标：加号。</summary>
    public static Bitmap CreatePlusBitmap(Color color, int size)
    {
        var bitmap = NewBitmap(size);
        using var graphics = Graphics.FromImage(bitmap);
        Prepare(graphics);
        using var pen = NewPen(color, size);

        float scale = size / 24f;
        graphics.DrawLine(pen, 6f * scale, 13f * scale, 18f * scale, 13f * scale);
        graphics.DrawLine(pen, 12f * scale, 7f * scale, 12f * scale, 19f * scale);
        return bitmap;
    }

    /// <summary>菜单图标：减号。</summary>
    public static Bitmap CreateMinusBitmap(Color color, int size)
    {
        var bitmap = NewBitmap(size);
        using var graphics = Graphics.FromImage(bitmap);
        Prepare(graphics);
        using var pen = NewPen(color, size);

        float scale = size / 24f;
        graphics.DrawLine(pen, 6f * scale, 13f * scale, 18f * scale, 13f * scale);
        return bitmap;
    }

    /// <summary>
    /// 应用图标（绿底 + ♪），托盘图标和设置窗口共用同一套画法。
    /// </summary>
    public static Bitmap CreateAppIconBitmap(int size = 32)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        float scale = size / 32f;

        using (var background = new SolidBrush(Color.FromArgb(255, 31, 190, 116))) // QQ 绿
        {
            graphics.FillEllipse(background, 1 * scale, 1 * scale, 30 * scale, 30 * scale);
        }

        using (var font = new Font("Segoe UI Symbol", 17f * scale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat
               {
                   Alignment = StringAlignment.Center,
                   LineAlignment = StringAlignment.Center,
               })
        {
            graphics.DrawString("♪", font, Brushes.White,
                new RectangleF(1 * scale, 2 * scale, 30 * scale, 30 * scale), format);
        }

        return bitmap;
    }

    /// <summary>菜单图标：齿轮（设置）。设计坐标 24×24：外圈 r=6.4、内圈 r=2.6，再加 8 根向外的齿。</summary>
    public static Bitmap CreateGearBitmap(Color color, int size)
    {
        var bitmap = NewBitmap(size);
        using var graphics = Graphics.FromImage(bitmap);
        Prepare(graphics);
        using var pen = NewPen(color, size);

        float scale = size / 24f;
        const float center = 12f;
        const float outerRadius = 6.2f;
        const float innerRadius = 2.6f;
        const float toothRadius = 8.4f;

        graphics.DrawEllipse(pen,
            (center - outerRadius) * scale, (center - outerRadius) * scale,
            outerRadius * 2 * scale, outerRadius * 2 * scale);
        graphics.DrawEllipse(pen,
            (center - innerRadius) * scale, (center - innerRadius) * scale,
            innerRadius * 2 * scale, innerRadius * 2 * scale);

        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4 + Math.PI / 8;
            float x1 = center + (float)(outerRadius * Math.Cos(angle));
            float y1 = center + (float)(outerRadius * Math.Sin(angle));
            float x2 = center + (float)(toothRadius * Math.Cos(angle));
            float y2 = center + (float)(toothRadius * Math.Sin(angle));
            graphics.DrawLine(pen, x1 * scale, y1 * scale, x2 * scale, y2 * scale);
        }

        return bitmap;
    }

    private static Bitmap NewBitmap(int size) => new(size, size, PixelFormat.Format32bppArgb);

    private static void Prepare(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
    }

    private static Pen NewPen(Color color, int size) => new(color, 2.0f * (size / 24f))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

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
