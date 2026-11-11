using System.Drawing.Drawing2D;

namespace Varco.App;

/// <summary>
/// Logo di Varco: due schermi i cui lati interni formano un varco a V, attraversato dal cursore.
/// Geometria su una griglia 128×128 (la stessa di assets/varco.svg).
/// Sotto i 32 px il cursore viene omesso e gli schermi si allungano: resta leggibile anche nella tray.
/// </summary>
public static class VarcoIcon
{
    public static readonly Color Brand = Color.FromArgb(0x53, 0x4A, 0xB7);

    private static readonly PointF[] LeftFull = [new(18, 28), new(46, 28), new(58, 92), new(18, 92)];
    private static readonly PointF[] RightFull = [new(110, 28), new(82, 28), new(70, 92), new(110, 92)];
    private static readonly PointF[] LeftSmall = [new(18, 26), new(46, 26), new(60, 102), new(18, 102)];
    private static readonly PointF[] RightSmall = [new(110, 26), new(82, 26), new(68, 102), new(110, 102)];
    // Cursore nella parte alta del varco: la punta della V resta visibile sotto.
    private static readonly PointF[] Cursor =
        [new(57, 34), new(57, 65.2f), new(65.4f, 58), new(71.4f, 71.2f), new(77.4f, 68.8f), new(71.4f, 55.6f), new(82.2f, 55.6f)];

    /// <summary>Icona dell'app: quadrato arrotondato viola.</summary>
    /// <param name="gradient">Sfumatura diagonale (finestre col tema scuro) invece del viola pieno.</param>
    public static Bitmap RenderApp(int size, bool gradient = false)
    {
        var bmp = new Bitmap(size, size);
        using var g = Prepare(bmp);
        using (Brush bg = gradient
                   ? new LinearGradientBrush(new RectangleF(0, 0, size, size), Color.FromArgb(0x8A, 0x82, 0xE6), Color.FromArgb(0x43, 0x3B, 0x9C), 45f)
                   : new SolidBrush(Brand))
        using (var path = RoundedSquare(0, 0, size, size * 0.225f))
            g.FillPath(bg, path);
        DrawGlyph(g, new RectangleF(0, 0, size, size), gradient ? Color.FromArgb(0x63, 0x5A, 0xC6) : Brand, withCursor: size >= 32);
        return bmp;
    }

    /// <summary>Icona della tray: cerchio nel colore di stato con gli schermi a V.</summary>
    public static Bitmap RenderTray(int size, Color state)
    {
        var bmp = new Bitmap(size, size);
        using var g = Prepare(bmp);
        using (var bg = new SolidBrush(state))
            g.FillEllipse(bg, 0.5f, 0.5f, size - 1, size - 1);
        var inner = size * 16f / 22f; // stesse proporzioni della proposta
        var off = (size - inner) / 2f;
        DrawGlyph(g, new RectangleF(off, off, inner, inner), state, withCursor: false);
        return bmp;
    }

    private static Graphics Prepare(Bitmap bmp)
    {
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        return g;
    }

    private static void DrawGlyph(Graphics g, RectangleF box, Color background, bool withCursor)
    {
        var state = g.Save();
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(box.Width / 128f, box.Height / 128f);

        using var white = new SolidBrush(Color.White);
        using var outline = new Pen(Color.White, 8) { LineJoin = LineJoin.Round };
        foreach (var poly in withCursor ? new[] { LeftFull, RightFull } : [LeftSmall, RightSmall])
        {
            g.FillPolygon(white, poly);
            g.DrawPolygon(outline, poly);
        }

        if (withCursor)
        {
            using var fill = new SolidBrush(background);
            using var edge = new Pen(Color.White, 4) { LineJoin = LineJoin.Round };
            g.FillPolygon(fill, Cursor);
            g.DrawPolygon(edge, Cursor);
        }
        g.Restore(state);
    }

    private static GraphicsPath RoundedSquare(float x, float y, float size, float r)
    {
        var p = new GraphicsPath();
        var d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + size - d, y, d, d, 270, 90);
        p.AddArc(x + size - d, y + size - d, d, d, 0, 90);
        p.AddArc(x, y + size - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
