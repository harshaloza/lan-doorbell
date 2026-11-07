using System.Drawing.Drawing2D;

namespace LanDoorbell;

/// <summary>
/// Draws the bell from Assets/design/logo.svg with GDI+, so the floating button
/// matches the app icon and stays sharp at every display scale.
/// Coordinates are in the SVG's 1024 × 1024 space.
/// </summary>
internal static class BellArt
{
    private static readonly Color Gold1 = Color.FromArgb(255, 246, 196);
    private static readonly Color Gold2 = Color.FromArgb(255, 215, 94);
    private static readonly Color Gold3 = Color.FromArgb(242, 168, 30);
    private static readonly Color Gold4 = Color.FromArgb(185, 121, 11);
    private static readonly Color WaveTop = Color.FromArgb(255, 224, 122);
    private static readonly Color WaveBottom = Color.FromArgb(243, 174, 36);

    public enum State { Normal, Hover, Pressed }

    /// <summary>Round button face: navy disc, bell, gold clapper and sound waves.</summary>
    public static void DrawButton(Graphics g, int size, State state)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var disc = new RectangleF(0, 0, size - 1, size - 1);

        (Color top, Color bottom) = state switch
        {
            State.Hover => (Color.FromArgb(46, 84, 150), Color.FromArgb(18, 38, 80)),
            State.Pressed => (Color.FromArgb(16, 32, 64), Color.FromArgb(8, 18, 40)),
            _ => (Color.FromArgb(34, 65, 122), Color.FromArgb(10, 22, 49)),
        };
        using (var bg = new LinearGradientBrush(disc, top, bottom, LinearGradientMode.Vertical))
            g.FillEllipse(bg, disc);
        using (var glow = new GraphicsPath())
        {
            glow.AddEllipse(size * 0.1f, size * 0.1f, size * 0.8f, size * 0.8f);
            using var brush = new PathGradientBrush(glow)
            {
                CenterColor = Color.FromArgb(state == State.Hover ? 120 : 80, 61, 184, 245),
                SurroundColors = new[] { Color.FromArgb(0, 61, 184, 245) },
            };
            g.FillPath(brush, glow);
        }
        using (var rim = new Pen(Color.FromArgb(110, 159, 220, 255), Math.Max(1f, size / 34f)))
            g.DrawEllipse(rim, size * 0.025f, size * 0.025f, size * 0.95f, size * 0.95f);

        var saved = g.Save();
        // Fit the bell and its inner waves (SVG x 224..800, y 176..762) into the disc.
        // The outer waves are left out so the bell can be bigger on a small button.
        float k = size / 700f;
        g.TranslateTransform(size / 2f, size / 2f);
        g.ScaleTransform(k, k);
        g.TranslateTransform(-512f, -469f);
        DrawWaves(g, includeOuter: false);
        DrawBell(g);
        g.Restore(saved);
    }

    private static void DrawWaves(Graphics g, bool includeOuter)
    {
        using var brush = new LinearGradientBrush(new RectangleF(0, 222, 1024, 300), WaveTop, WaveBottom, LinearGradientMode.Vertical);
        using var inner = new Pen(brush, 30) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var outerBrush = new LinearGradientBrush(new RectangleF(0, 222, 1024, 300),
            Color.FromArgb(150, WaveTop), Color.FromArgb(150, WaveBottom), LinearGradientMode.Vertical);
        using var outer = new Pen(outerBrush, 26) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // SVG quadratic curves (shifted up 40 like the logo), as cubic Béziers.
        foreach (float side in new[] { 1f, -1f })
        {
            PointF M(float x, float y) => new(side > 0 ? x : 1024 - x, y - 40);
            g.DrawBezier(inner, M(286, 316), M(250, 362.7f), M(238, 416), M(250, 476));
            if (includeOuter) g.DrawBezier(outer, M(210, 262), M(162, 343.3f), M(147.3f, 429.3f), M(166, 520));
        }
    }

    private static void DrawBell(Graphics g)
    {
        g.TranslateTransform(0, -40);

        // Crown / hanging loop
        using (var crown = RoundedRect(486, 216, 52, 62, 26))
        using (var rimBrush = RimBrush(216, 278))
            g.FillPath(rimBrush, crown);

        using var body = BellBody();
        using (var bellBrush = new LinearGradientBrush(new PointF(290, 0), new PointF(734, 0), Color.Black, Color.Black))
        {
            bellBrush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(79, 195, 247), Color.FromArgb(155, 230, 255), Color.FromArgb(77, 183, 239),
                    Color.FromArgb(35, 133, 210), Color.FromArgb(22, 99, 174),
                },
                Positions = new[] { 0f, 0.28f, 0.52f, 0.82f, 1f },
            };
            g.FillPath(bellBrush, body);
        }
        using (var shade = new LinearGradientBrush(new PointF(0, 262), new PointF(0, 681), Color.FromArgb(0, 11, 60, 120), Color.FromArgb(97, 11, 60, 120)))
            g.FillPath(shade, body);

        // Shoulder band
        using (var band = new Pen(Color.FromArgb(90, 255, 255, 255), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawBezier(band, new PointF(369, 404), new PointF(463, 391), new PointF(561, 391), new PointF(655, 404));

        // Specular highlight
        using (var hl = new GraphicsPath())
        {
            hl.AddBezier(432, 316, 404, 340, 392, 382, 386, 424);
            hl.AddLine(386, 424, 372, 540);
            hl.AddBezier(372, 540, 368, 576, 360, 604, 344, 628);
            hl.AddBezier(344, 628, 366, 614, 382, 588, 390, 556);
            hl.AddLine(390, 556, 406, 432);
            hl.AddBezier(406, 432, 412, 386, 420, 348, 432, 316);
            hl.CloseFigure();
            using var brush = new LinearGradientBrush(new PointF(0, 300), new PointF(0, 641), Color.FromArgb(215, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            g.FillPath(brush, hl);
        }

        // Lip
        using (var lip = RoundedRect(268, 664, 488, 50, 25))
        using (var rimBrush = RimBrush(664, 714))
            g.FillPath(rimBrush, lip);
        using (var lipLight = new Pen(Color.FromArgb(115, 255, 255, 255), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(lipLight, 298, 673, 726, 673);

        // Clapper
        using (var stem = new SolidBrush(Color.FromArgb(14, 74, 140)))
            g.FillRectangle(stem, 503, 712, 18, 24);
        using (var clapper = new GraphicsPath())
        {
            clapper.AddEllipse(466, 710, 92, 92);
            using var gold = new PathGradientBrush(clapper)
            {
                CenterPoint = new PointF(496, 736),
                CenterColor = Gold1,
            };
            gold.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Gold4, Gold3, Gold2, Gold1 },
                Positions = new[] { 0f, 0.3f, 0.65f, 1f },
            };
            g.FillPath(gold, clapper);
        }
    }

    private static GraphicsPath BellBody()
    {
        var p = new GraphicsPath();
        p.AddBezier(512, 262, 436, 262, 378, 312, 366, 404);
        p.AddLine(366, 404, 350, 520);
        p.AddBezier(350, 520, 342, 578, 322, 616, 290, 646);
        p.AddBezier(290, 646, 276, 659, 284, 680, 304, 680);
        p.AddLine(304, 680, 720, 680);
        p.AddBezier(720, 680, 740, 680, 748, 659, 734, 646);
        p.AddBezier(734, 646, 702, 616, 682, 578, 674, 520);
        p.AddLine(674, 520, 658, 404);
        p.AddBezier(658, 404, 646, 312, 588, 262, 512, 262);
        p.CloseFigure();
        return p;
    }

    private static LinearGradientBrush RimBrush(float top, float bottom)
    {
        var brush = new LinearGradientBrush(new PointF(0, top - 0.5f), new PointF(0, bottom + 0.5f), Color.Black, Color.Black);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = new[] { Color.FromArgb(63, 166, 230), Color.FromArgb(30, 120, 198), Color.FromArgb(17, 80, 143) },
            Positions = new[] { 0f, 0.5f, 1f },
        };
        return brush;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
