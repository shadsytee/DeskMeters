using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DeskMeters
{
    /// <summary>
    /// Drawing primitives for the meters, all driven by the current <see cref="Skin"/>.
    /// Rendering is single-threaded (UI thread), so a static "current skin" is enough:
    /// each window calls <see cref="Use"/> before it draws.
    /// </summary>
    internal static class Theme
    {
        public static Skin Skin { get; private set; } = Skins.Get(Skins.DefaultId);

        public static void Use(Skin skin) => Skin = skin ?? Skins.Get(Skins.DefaultId);

        public const float ShadowMargin = 10f;

        // ---- forwarded skin values ----------------------------------------------------------

        public static float Width => Skin.Width;
        public static float Pad => Skin.Pad;
        public static float Radius => Skin.Radius;
        public static float Row => Skin.Row;
        public static float Gap => Skin.Gap;
        public static float ThreadRow => Skin.ThreadRow;
        public static bool Compact => Skin.Compact;

        /// <summary>A fixed layout size at the current density, rounded to whole pixels.</summary>
        public static float K(float v) => Skin.Shrink == 1f ? v : (float)Math.Round(v * Skin.Shrink);

        /// <summary>A history graph height at the current density.</summary>
        public static float G(float v) => Skin.GraphScale == 1f ? v : (float)Math.Round(v * Skin.GraphScale);

        /// <summary>Use ClearType where the design allows it (tray setting).</summary>
        public static bool SharpTextEnabled = true;
        public static float Margin => Skin.Shadow ? ShadowMargin : 0f;
        public static MeterLayout Layout => Skin.Layout;

        public static Color PanelTop => Skin.PanelTop;
        public static Color PanelBottom => Skin.PanelBottom;
        public static Color Border => Skin.Border;
        public static Color Highlight => Skin.Highlight;
        public static Color Rule => Skin.Rule;
        public static Color Card => Skin.Card;
        public static Color Title => Skin.Title;
        public static Color Fore => Skin.Fore;
        public static Color Dim => Skin.Dim;
        public static Color Faint => Skin.Faint;
        public static Color Green => Skin.Green;
        public static Color Yellow => Skin.Yellow;
        public static Color Orange => Skin.Orange;
        public static Color Red => Skin.Red;
        public static Color Cyan => Skin.Cyan;
        public static Color Blue => Skin.Blue;
        public static Color Purple => Skin.Purple;
        public static Color Track => Skin.Track;
        public static Color GraphBack => Skin.GraphBack;
        public static Color Grid => Skin.Grid;
        public static Color Accent => Skin.Accent;
        public static Color LinkColor => Skin.Link;

        // ---- fonts ------------------------------------------------------------------------------

        private static Font F(float px, bool bold = false, FontStyle extra = FontStyle.Regular)
        {
            string family = bold ? Skin.BoldFamily : Skin.FontFamily;
            FontStyle style = extra;
            if (bold && string.Equals(Skin.BoldFamily, Skin.FontFamily, StringComparison.OrdinalIgnoreCase) && !Skin.Monospace)
                style |= FontStyle.Bold;
            return Fonts.Get(family, px * Skin.FontScale, style);
        }

        public static Font Header => F(Skin.TitlePx, true);
        public static Font HeaderValue => F(Skin.ValuePx, true);
        public static Font Medium => F(Skin.MediumPx, true);
        public static Font Big => F(Skin.BigPx, true);
        public static Font Huge => F(Skin.HugePx, true);
        public static Font Body => F(Skin.BodyPx);
        public static Font BodyBold => F(Skin.BodyPx, true);
        public static Font Small => F(Skin.SmallPx);
        public static Font SmallBold => F(Skin.SmallPx, true);
        public static Font Caption => F(Skin.CaptionPx, true);
        public static Font LinkFont => F(Skin.LinkPx);
        public static Font LinkHover => F(Skin.LinkPx, false, FontStyle.Underline);
        public static Font LinkBold => F(Skin.LinkPx, true);
        public static Font LinkBoldHover => F(Skin.LinkPx, true, FontStyle.Underline);
        public static Font TileValue => F(Skin.TilePx, true);
        public static Font Tiny => F(Skin.Compact ? 7f : 7.5f);

        /// <summary>Labels inside bars: semibold at compact size so they stay readable.</summary>
        private static Font BarLabel => Skin.Compact ? SmallBold : Small;

        private static readonly StringFormat Left = Make(StringAlignment.Near);
        private static readonly StringFormat Right = Make(StringAlignment.Far);
        private static readonly StringFormat Center = Make(StringAlignment.Center);

        private static StringFormat Make(StringAlignment a)
        {
            var f = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = a,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
            };
            // GenericTypographic sets LineLimit (drops a line that doesn't fully fit the
            // rectangle) and NoClip (lets text spill past it). We want neither.
            f.FormatFlags &= ~(StringFormatFlags.LineLimit | StringFormatFlags.NoClip);
            f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            return f;
        }

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            // ClearType can't blend onto a transparent bitmap; greyscale AA can. Designs whose
            // text always sits on an opaque panel (SharpText) get ClearType, as in the gadgets.
            g.TextRenderingHint = Skin.SharpText && SharpTextEnabled
                ? TextRenderingHint.ClearTypeGridFit
                : TextRenderingHint.AntiAliasGridFit;
        }

        // ---- shapes ---------------------------------------------------------------------------

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Rectangle with the top-left and bottom-right corners cut off (HUD).</summary>
        public static GraphicsPath Clipped(RectangleF r, float cut)
        {
            var p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(r.X + cut, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom - cut),
                new PointF(r.Right - cut, r.Bottom), new PointF(r.X, r.Bottom), new PointF(r.X, r.Y + cut),
            });
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            if (c.A == 0) return;
            using (GraphicsPath p = Rounded(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void Outline(Graphics g, RectangleF r, float radius, Color c, float width = 1f)
        {
            if (c.A == 0) return;
            using (GraphicsPath p = Rounded(r, radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        // ---- panel ------------------------------------------------------------------------------

        public static void Panel(Graphics g, float w, float h)
        {
            if (Skin.Shadow)
            {
                for (int i = (int)ShadowMargin; i >= 1; i--)
                {
                    float t = 1f - i / ShadowMargin;
                    int a = (int)(36 * t * t);
                    if (a <= 0) continue;
                    Fill(g, new RectangleF(-i, -i + 3, w + i * 2, h + i * 2), Radius + i, Color.FromArgb(a, 0, 0, 0));
                }
            }

            switch (Skin.PanelStyle)
            {
                case PanelStyle.None:
                    return;

                case PanelStyle.Clipped:
                    PanelClipped(g, w, h);
                    return;

                case PanelStyle.Bezel:
                    PanelBezel(g, w, h);
                    return;
            }

            var r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using (GraphicsPath path = Rounded(r, Radius))
            {
                using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, h), PanelTop, PanelBottom))
                    g.FillPath(brush, path);

                if (Skin.PanelStyle == PanelStyle.Glass && Skin.Sheen)
                {
                    using (var sheen = new LinearGradientBrush(new PointF(0, 0), new PointF(0, 30), Color.FromArgb(22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                    {
                        GraphicsState st = g.Save();
                        g.SetClip(path);
                        g.FillRectangle(sheen, 0, 0, w, 30);
                        g.Restore(st);
                    }
                }

                using (var pen = new Pen(Border, 1f))
                    g.DrawPath(pen, path);
            }

            if (Highlight.A > 0)
            {
                using (GraphicsPath inner = Rounded(new RectangleF(1.5f, 1.5f, w - 3, h - 3), Math.Max(0, Radius - 1)))
                using (var pen = new Pen(Highlight, 1f))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(new RectangleF(0, 0, w, 14));
                    g.DrawPath(pen, inner);
                    g.Restore(st);
                }
            }
        }

        private static void PanelClipped(Graphics g, float w, float h)
        {
            float cut = K(13f);
            var r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using (GraphicsPath path = Clipped(r, cut))
            {
                using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, h), PanelTop, PanelBottom))
                    g.FillPath(brush, path);
                using (var pen = new Pen(Border, 1f))
                    g.DrawPath(pen, path);
                // Outer glow line
                using (GraphicsPath outer = Clipped(new RectangleF(-1, -1, w + 2, h + 2), cut + 1))
                using (var pen = new Pen(Color.FromArgb(40, Accent), 2f))
                    g.DrawPath(pen, outer);
            }
            // Corner accents
            using (var pen = new Pen(Accent, 2f))
            {
                g.DrawLine(pen, cut + 1, 1, cut + K(22), 1);
                g.DrawLine(pen, w - cut - 1, h - 1, w - cut - K(22), h - 1);
            }
        }

        private static void PanelBezel(Graphics g, float w, float h)
        {
            var r = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
            using (GraphicsPath path = Rounded(r, Radius))
            {
                using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, h), PanelTop, PanelBottom))
                    g.FillPath(brush, path);
                using (var pen = new Pen(Border, 1f))
                    g.DrawPath(pen, path);
            }
            using (GraphicsPath inner = Rounded(new RectangleF(1.5f, 1.5f, w - 3, h - 3), Math.Max(0, Radius - 1)))
            using (var pen = new Pen(Highlight, 1f))
            {
                GraphicsState st = g.Save();
                g.SetClip(new RectangleF(0, 0, w, 10));
                g.DrawPath(pen, inner);
                g.Restore(st);
            }

            // The amber screen, inset from the bezel
            float inset = Skin.Compact ? 5f : 7f;
            var screen = new RectangleF(inset, inset, w - inset * 2, h - inset * 2);
            using (GraphicsPath sp = Rounded(screen, 4f))
            {
                using (var b = new SolidBrush(Color.FromArgb(31, 22, 6)))
                    g.FillPath(b, sp);
                GraphicsState st = g.Save();
                g.SetClip(sp);
                using (var scan = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                    for (float y = screen.Y; y < screen.Bottom; y += 3)
                        g.FillRectangle(scan, screen.X, y, screen.Width, 1);
                // inner shadow at the top
                using (var sh = new LinearGradientBrush(new PointF(0, screen.Y), new PointF(0, screen.Y + 8), Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)))
                    g.FillRectangle(sh, screen.X, screen.Y, screen.Width, 8);
                g.Restore(st);
                using (var pen = new Pen(Color.FromArgb(200, 8, 8, 9), 2f))
                    g.DrawPath(pen, sp);
            }
        }

        // ---- text -------------------------------------------------------------------------------

        public static void Text(Graphics g, string s, Font f, Color c, float x, float y, float w, float h = -1, StringAlignment align = StringAlignment.Near)
        {
            if (string.IsNullOrEmpty(s) || w <= 0) return;
            if (h < 0) h = Row;
            if (Skin.Uppercase) s = s.ToUpperInvariant();
            StringFormat fmt = align == StringAlignment.Far ? Right : align == StringAlignment.Center ? Center : Left;
            var rect = new RectangleF(x, y, w, h);

            if (Skin.TextShadow)
            {
                using (var sb = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                {
                    g.DrawString(s, f, sb, new RectangleF(x + 1, y + 1, w, h), fmt);
                    g.DrawString(s, f, sb, new RectangleF(x, y + 1, w, h), fmt);
                }
            }
            else if (Skin.Glow && f.Size >= 12 * Skin.FontScale)
            {
                using (var gb = new SolidBrush(Color.FromArgb(70, c)))
                {
                    g.DrawString(s, f, gb, new RectangleF(x + 1, y, w, h), fmt);
                    g.DrawString(s, f, gb, new RectangleF(x - 1, y, w, h), fmt);
                    g.DrawString(s, f, gb, new RectangleF(x, y + 1, w, h), fmt);
                    g.DrawString(s, f, gb, new RectangleF(x, y - 1, w, h), fmt);
                }
            }

            using (var b = new SolidBrush(c))
                g.DrawString(s, f, b, rect, fmt);
        }

        private static string Case(string s) => Skin.Uppercase ? s.ToUpperInvariant() : s;

        public static float Measure(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (Skin.Uppercase) s = s.ToUpperInvariant();
            return g.MeasureString(s, f, 2000, Left).Width;
        }

        public static void Divider(Graphics g, float y, float w)
        {
            if (Rule.A == 0) return;
            if (Skin.Monospace && Skin.BarStyle == BarStyle.Blocks)
            {
                // Terminal: a line of box-drawing characters
                float cw = Measure(g, "─", Body);
                int n = Math.Max(1, (int)((w - Pad * 2) / Math.Max(1, cw)));
                Text(g, new string('─', n), Body, Faint, Pad, y - Row / 2, w - Pad * 2, Row);
                return;
            }
            float ly = (float)Math.Floor(y) + 0.5f;   // whole pixel, so the hairline stays crisp
            using (var pen = new Pen(Rule, 1f))
                g.DrawLine(pen, Pad, ly, w - Pad, ly);
        }

        /// <summary>Small caption with a hairline running to the right edge. Returns the y below it.</summary>
        public static float Section(Graphics g, float y, float w, string label, string right = null)
        {
            float sh = K(12);
            Text(g, label.ToUpperInvariant(), Caption, Faint, Pad, y, w - Pad * 2, sh);
            float tw = Measure(g, label.ToUpperInvariant(), Caption);
            float rw = 0;
            if (!string.IsNullOrEmpty(right))
            {
                Text(g, right, Caption, Faint, Pad, y, w - Pad * 2, sh, StringAlignment.Far);
                rw = Measure(g, right, Caption) + 6;
            }
            if (Rule.A > 0)
                using (var pen = new Pen(Rule, 1f))
                {
                    float x0 = Pad + tw + 6, x1 = w - Pad - rw;
                    float ly = (float)Math.Floor(y + sh / 2) + 0.5f;
                    if (x1 > x0) g.DrawLine(pen, x0, ly, x1, ly);
                }
            return y + K(14);
        }

        /// <summary>Rounded stat tile: caption on top, value below. Used by the Light design.</summary>
        public static void Tile(Graphics g, RectangleF r, string caption, string value, Color valueColor, string sub = null)
        {
            Fill(g, r, K(6), Card);
            float ix = K(6);
            Text(g, caption.ToUpperInvariant(), Caption, Faint, r.X + ix, r.Y + K(2), r.Width - ix - 2, K(11));
            Text(g, value, TileValue, valueColor, r.X + ix, r.Y + K(12), r.Width - ix - 2, K(15));
            if (!string.IsNullOrEmpty(sub))
                Text(g, sub, Small, Dim, r.X + ix, r.Y + K(26), r.Width - ix - 2, K(11));
        }

        public static float TileHeight(bool withSub) => withSub ? K(40) : K(30);

        // ---- bars ------------------------------------------------------------------------------

        /// <summary>Height a bar with labels takes in the current style.</summary>
        public static float BarHeight(bool withLabels = true)
        {
            switch (Skin.BarStyle)
            {
                case BarStyle.Blocks: return withLabels ? Row * 2 : Row;
                case BarStyle.Lcd: return withLabels ? Row + 1 + LcdCell : LcdCell;
                case BarStyle.Thin: return withLabels ? Row + 3 : 3;
                case BarStyle.Gradient: return Skin.BarH;
                default: return Skin.SlimBarH;
            }
        }

        private static float LcdCell => Skin.Compact ? 6f : 7f;

        /// <summary>Horizontal bar with optional labels. Returns the height used.</summary>
        public static float Bar(Graphics g, float x, float y, float w, float pct, Color fill, string leftLabel = null, string rightLabel = null)
        {
            pct = Fmt.Clamp(pct, 0, 100);
            bool labels = leftLabel != null || rightLabel != null;
            Color labelColor = Color.FromArgb(240, 245, 250);

            switch (Skin.BarStyle)
            {
                case BarStyle.Blocks:
                {
                    float ty = y;
                    if (labels)
                    {
                        if (leftLabel != null) Text(g, leftLabel, Small, Dim, x, ty, w, Row);
                        if (rightLabel != null) Text(g, rightLabel, Small, fill, x, ty, w, Row, StringAlignment.Far);
                        ty += Row;
                    }
                    float cw = Math.Max(1, Measure(g, "█", Small));
                    int n = Math.Max(4, (int)(w / cw));
                    int k = (int)Math.Round(pct / 100 * n);
                    Text(g, new string('█', k), Small, fill, x, ty, w + 2, Row);
                    Text(g, new string('░', n - k), Small, Faint, x + k * cw, ty, w - k * cw + 2, Row);
                    return ty + Row - y;
                }

                case BarStyle.Lcd:
                {
                    float ty = y;
                    if (labels)
                    {
                        if (leftLabel != null) Text(g, leftLabel, Small, Fore, x, ty, w, Row);
                        if (rightLabel != null) Text(g, rightLabel, Small, Fore, x, ty, w, Row, StringAlignment.Far);
                        ty += Row + 1;
                    }
                    int n = Math.Max(8, (int)(w / 6));
                    float cell = (w - (n - 1)) / n;
                    int k = pct <= 0 ? 0 : Math.Max(1, (int)Math.Round(pct / 100 * n));
                    using (var on = new SolidBrush(fill))
                    using (var off = new SolidBrush(Track))
                        for (int i = 0; i < n; i++)
                            g.FillRectangle(i < k ? on : off, x + i * (cell + 1), ty, cell, LcdCell);
                    return ty + LcdCell - y;
                }

                case BarStyle.Thin:
                {
                    float ty = y;
                    if (labels)
                    {
                        if (leftLabel != null) Text(g, leftLabel, Small, Dim, x, ty, w, Row);
                        if (rightLabel != null) Text(g, rightLabel, Small, Fore, x, ty, w, Row, StringAlignment.Far);
                        ty += Row;
                    }
                    using (var sh = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                        g.FillRectangle(sh, x, ty + 1, w, 2);
                    using (var tb = new SolidBrush(Track))
                        g.FillRectangle(tb, x, ty, w, 2);
                    using (var fb = new SolidBrush(fill))
                        g.FillRectangle(fb, x, ty, w * pct / 100f, 2);
                    return ty + 3 - y;
                }

                case BarStyle.Segmented:
                {
                    float h = Skin.SlimBarH;
                    var r = new RectangleF(x, y, w, h);
                    Fill(g, r, 2f, Track);
                    float fw = w * pct / 100f;
                    if (fw >= 1)
                    {
                        GraphicsState st = g.Save();
                        g.SetClip(new RectangleF(x, y, fw, h));
                        using (var fb = new SolidBrush(fill))
                            for (float sx = x; sx < x + w; sx += 5)
                                g.FillRectangle(fb, sx, y, 4, h);
                        g.Restore(st);
                    }
                    // A dark halo keeps the labels legible over the segments.
                    using (var halo = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    {
                        if (leftLabel != null) g.DrawString(Case(leftLabel), BarLabel, halo, new RectangleF(x + 5, y + 1, w - 8, h), Left);
                        if (rightLabel != null) g.DrawString(Case(rightLabel), BarLabel, halo, new RectangleF(x + 4, y + 1, w - 7, h), Right);
                    }
                    if (leftLabel != null) Text(g, leftLabel, BarLabel, labelColor, x + 4, y, w - 8, h);
                    if (rightLabel != null) Text(g, rightLabel, BarLabel, labelColor, x + 4, y, w - 8, h, StringAlignment.Far);
                    return h;
                }

                case BarStyle.Flat:
                {
                    float h = Skin.SlimBarH;
                    var r = new RectangleF(x, y, w, h);
                    float radius = h / 2;
                    Fill(g, r, radius, Track);
                    float fw = w * pct / 100f;
                    if (fw >= 1) Fill(g, new RectangleF(x, y, Math.Max(fw, h), h), radius, fill);
                    // Light text once the fill reaches under a label, dark text otherwise.
                    if (leftLabel != null) Text(g, leftLabel, BarLabel, pct >= 45 ? Color.White : Title, x + 5, y, w - 10, h);
                    if (rightLabel != null) Text(g, rightLabel, BarLabel, pct >= 90 ? Color.White : Title, x + 5, y, w - 10, h, StringAlignment.Far);
                    return h;
                }

                default: // Gradient
                {
                    float h = Skin.BarH;
                    var r = new RectangleF(x, y, w, h);
                    Fill(g, r, 3f, Track);
                    float fw = w * pct / 100f;
                    if (fw >= 1f)
                    {
                        var fr = new RectangleF(x, y, fw, h);
                        using (GraphicsPath fp = Rounded(fr, 3f))
                        using (var fb = new LinearGradientBrush(new PointF(0, y), new PointF(0, y + h + 0.01f), Lighten(fill, 0.35f, 255), Darken(fill, 0.15f, 255)))
                            g.FillPath(fb, fp);
                    }
                    Color lc = Color.FromArgb(242, 245, 248);
                    float lx = Skin.Compact ? 4 : 5;
                    if (leftLabel != null) Text(g, leftLabel, BarLabel, lc, x + lx, y, w - lx * 2, h);
                    if (rightLabel != null) Text(g, rightLabel, BarLabel, lc, x + lx, y, w - lx * 2, h, StringAlignment.Far);
                    return h;
                }
            }
        }

        // ---- graphs ----------------------------------------------------------------------------

        /// <summary>
        /// History graph. Series draw back to front, newest sample on the right.
        /// With <paramref name="mirror"/> and two series, the first grows up from the centre
        /// and the second down (used for upload/download in the Equalizer design).
        /// </summary>
        public static void Graph(Graphics g, RectangleF r, float max, bool mirror, params (Ring data, Color color, bool fill, float width)[] series)
        {
            if (max <= 0) max = 1;
            switch (Skin.GraphStyle)
            {
                case GraphStyle.HeatStrip: GraphHeat(g, r, max, series); return;
                case GraphStyle.Columns: GraphColumns(g, r, max, mirror, series, false); return;
                case GraphStyle.LcdColumns: GraphColumns(g, r, max, mirror, series, true); return;
            }

            float radius = Skin.Radius > 0 ? 4f : 0f;
            Fill(g, r, radius, GraphBack);

            if (Grid.A > 0)
                using (var grid = new Pen(Grid, 1f))
                {
                    for (int i = 1; i < 4; i++)
                    {
                        float y = r.Y + r.Height * i / 4f;
                        g.DrawLine(grid, r.X + 1, y, r.Right - 1, y);
                    }
                    if (Skin.HeaderRule)
                        for (int i = 1; i < 5; i++)
                        {
                            float x = r.X + r.Width * i / 5f;
                            g.DrawLine(grid, x, r.Y + 1, x, r.Bottom - 1);
                        }
                    else
                        for (float x = r.Right - 12; x > r.X; x -= 12)
                            g.DrawLine(grid, x, r.Y + 1, x, r.Bottom - 1);
                }

            GraphicsState st = g.Save();
            using (GraphicsPath clip = Rounded(r, radius))
                g.SetClip(clip);

            const float step = 2f;
            int slots = (int)(r.Width / step) + 1;
            bool lineOnly = Skin.GraphStyle == GraphStyle.Line;

            foreach (var s in series)
            {
                Ring data = s.data;
                if (data == null || data.Count < 2) continue;
                int n = Math.Min(slots, data.Count);
                var pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    float v = data[data.Count - n + i];
                    float x = r.Right - (n - 1 - i) * step;
                    float y = r.Bottom - 1 - (r.Height - 3) * Fmt.Clamp(v / max, 0, 1);
                    pts[i] = new PointF(x, y);
                }

                if (s.fill && !lineOnly)
                {
                    using (var area = new GraphicsPath())
                    {
                        area.AddLines(pts);
                        area.AddLine(pts[n - 1].X, pts[n - 1].Y, pts[n - 1].X, r.Bottom);
                        area.AddLine(pts[n - 1].X, r.Bottom, pts[0].X, r.Bottom);
                        area.CloseFigure();
                        if (Skin.HeaderRule)
                            using (var ab = new SolidBrush(Color.FromArgb(71, s.color)))
                                g.FillPath(ab, area);
                        else
                            using (var ab = new LinearGradientBrush(new PointF(0, r.Y), new PointF(0, r.Bottom + 0.01f), Color.FromArgb(110, s.color), Color.FromArgb(18, s.color)))
                                g.FillPath(ab, area);
                    }
                }

                if (Skin.TextShadow)
                    using (var sp = new Pen(Color.FromArgb(140, 0, 0, 0), s.width + 1f) { LineJoin = LineJoin.Round })
                    {
                        var shadow = new PointF[n];
                        for (int i = 0; i < n; i++) shadow[i] = new PointF(pts[i].X, pts[i].Y + 1.2f);
                        g.DrawLines(sp, shadow);
                    }

                int alpha = s.fill ? 245 : s.color.A < 255 ? s.color.A : 150;
                using (var pen = new Pen(Color.FromArgb(alpha, s.color), s.width) { LineJoin = LineJoin.Round })
                    g.DrawLines(pen, pts);
            }

            g.Restore(st);

            if (Skin.GraphStyle == GraphStyle.Area && GraphBack.A > 0)
                Outline(g, r, radius, Color.FromArgb(Skin.HeaderRule ? 31 : 40, 255, 255, 255));
            else if (lineOnly && Skin.PanelStyle == PanelStyle.Flat)
                Outline(g, r, 0, Rule);
        }

        public static void Graph(Graphics g, RectangleF r, float max, params (Ring data, Color color, bool fill, float width)[] series) =>
            Graph(g, r, max, false, series);

        private static void GraphHeat(Graphics g, RectangleF r, float max, (Ring data, Color color, bool fill, float width)[] series)
        {
            int rows = 0;
            foreach (var s in series) if (s.data != null && s.data.Count > 0 && (s.fill || series.Length == 1)) rows++;
            if (rows == 0) rows = 1;
            float rowH = (r.Height - (rows - 1) * 2) / rows;
            const float cellW = 5f, gap = 1f;
            int n = (int)((r.Width + gap) / (cellW + gap));
            float ry = r.Y;
            foreach (var s in series)
            {
                if (s.data == null || s.data.Count == 0 || !(s.fill || series.Length == 1)) continue;
                int count = Math.Min(n, s.data.Count);
                // Contrast comes from each series' own recent peak, floored so idle noise stays dark.
                float peak = Math.Max(s.data.Max(count), max * 0.15f);
                for (int i = 0; i < n; i++)
                {
                    float x = r.Right - (n - i) * (cellW + gap) + gap;
                    int idx = s.data.Count - count + (i - (n - count));
                    float v = i >= n - count ? Fmt.Clamp(s.data[idx] / peak, 0, 1) : 0;
                    Color c = i >= n - count ? Color.FromArgb((int)(20 + 225 * v), s.color) : Color.FromArgb(14, 255, 255, 255);
                    using (var b = new SolidBrush(c))
                        g.FillRectangle(b, x, ry, cellW, rowH);
                }
                ry += rowH + 2;
            }
        }

        private static void GraphColumns(Graphics g, RectangleF r, float max, bool mirror, (Ring data, Color color, bool fill, float width)[] series, bool lcd)
        {
            if (!lcd) Fill(g, r, 4f, GraphBack);
            const float colW = 3f, gap = 1f;
            int n = (int)((r.Width + gap) / (colW + gap));
            int segs = lcd ? 8 : 0;

            void Column(Ring data, Color color, float baseY, float height, int dir)
            {
                if (data == null || data.Count == 0) return;
                int count = Math.Min(n, data.Count);
                for (int i = n - count; i < n; i++)
                {
                    float v = Fmt.Clamp(data[data.Count - (n - i)] / max, 0, 1);
                    float x = r.Right - (n - i) * (colW + gap) + gap;
                    if (lcd)
                    {
                        float segH = (height - (segs - 1)) / segs;
                        int lit = (int)Math.Round(v * segs);
                        using (var on = new SolidBrush(color))
                        using (var off = new SolidBrush(Track))
                            for (int s = 0; s < segs; s++)
                            {
                                float y = dir < 0 ? baseY - (s + 1) * segH - s : baseY + s * (segH + 1);
                                g.FillRectangle(s < lit ? on : off, x, y, colW, segH);
                            }
                    }
                    else
                    {
                        float h = Math.Max(v > 0.01f ? 1 : 0, height * v);
                        if (h <= 0) continue;
                        float y = dir < 0 ? baseY - h : baseY;
                        using (var b = new LinearGradientBrush(new PointF(0, baseY - height), new PointF(0, baseY + height), Lighten(color, 0.3f, 255), Darken(color, 0.2f, 255)))
                            g.FillRectangle(b, x, y, colW, h);
                    }
                }
            }

            if (mirror && series.Length >= 2)
            {
                float mid = r.Y + r.Height / 2;
                Column(series[0].data, series[0].color, mid - 1, r.Height / 2 - 2, -1);
                Column(series[1].data, series[1].color, mid + 1, r.Height / 2 - 2, +1);
                using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                    g.DrawLine(pen, r.X + 2, mid, r.Right - 2, mid);
            }
            else
            {
                // Columns for the filled series (the "main" one); others drawn as a thin line on top.
                foreach (var s in series)
                    if (s.fill) Column(s.data, s.color, r.Bottom - 2, r.Height - 4, -1);
                if (!lcd)
                    foreach (var s in series)
                    {
                        if (s.fill || s.data == null || s.data.Count < 2) continue;
                        int count = Math.Min(n, s.data.Count);
                        var pts = new PointF[count];
                        for (int i = 0; i < count; i++)
                        {
                            float v = Fmt.Clamp(s.data[s.data.Count - count + i] / max, 0, 1);
                            pts[i] = new PointF(r.Right - (count - i) * (colW + gap) + gap + colW / 2, r.Bottom - 2 - (r.Height - 4) * v);
                        }
                        using (var pen = new Pen(Color.FromArgb(170, s.color), s.width))
                            g.DrawLines(pen, pts);
                    }
            }
        }

        /// <summary>Tiny history sparkline used for each CPU thread row: a thin line over a soft fill.</summary>
        public static void Spark(Graphics g, RectangleF r, Ring data, Color color)
        {
            bool light = Skin.PanelStyle == PanelStyle.Light;
            Fill(g, r, 2f, light ? Track : Color.FromArgb(13, 255, 255, 255));
            if (data == null || data.Count < 2) return;

            const float step = 2f;
            int slots = (int)(r.Width / step) + 1;
            int n = Math.Min(slots, data.Count);
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float v = Fmt.Clamp(data[data.Count - n + i], 0, 100);
                pts[i] = new PointF(r.Right - (n - 1 - i) * step, r.Bottom - 0.5f - (r.Height - 1.5f) * v / 100f);
            }

            GraphicsState st = g.Save();
            g.SetClip(r);
            using (var area = new GraphicsPath())
            {
                area.AddLines(pts);
                area.AddLine(pts[n - 1].X, pts[n - 1].Y, pts[n - 1].X, r.Bottom);
                area.AddLine(pts[n - 1].X, r.Bottom, pts[0].X, r.Bottom);
                area.CloseFigure();
                using (var ab = new SolidBrush(Color.FromArgb(light ? 50 : 89, color)))
                    g.FillPath(ab, area);
            }
            using (var pen = new Pen(color, 1f) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
            g.Restore(st);
        }

        /// <summary>Ring gauge; pct 0–100. Returns nothing, draws the track and the arc.</summary>
        public static void Ring(Graphics g, float cx, float cy, float radius, float thickness, float pct, Color color, Color? track = null)
        {
            var rect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using (var tp = new Pen(track ?? Track, thickness))
                g.DrawEllipse(tp, rect);
            float sweep = 360f * Fmt.Clamp(pct, 0, 100) / 100f;
            if (sweep < 1f) return;
            using (var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, rect, -90, sweep);
        }

        // ---- colour helpers ----------------------------------------------------------------------

        public static Color LoadColor(float pct)
        {
            if (pct >= 85) return Red;
            if (pct >= 60) return Orange;
            if (pct >= 30) return Yellow;
            return Green;
        }

        public static Color TempColor(float? c)
        {
            if (!c.HasValue) return Dim;
            if (c >= 85) return Red;
            if (c >= 72) return Orange;
            if (c >= 58) return Yellow;
            return Green;
        }

        public static Color FullnessColor(float pct)
        {
            if (pct >= 92) return Red;
            if (pct >= 80) return Orange;
            return Blue;
        }

        public static Color Series(int i, int n) => Skin.Series(i, n);

        public static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            double r = 0, gg = 0, b = 0;
            if (h < 60) { r = c; gg = x; }
            else if (h < 120) { r = x; gg = c; }
            else if (h < 180) { gg = c; b = x; }
            else if (h < 240) { gg = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb((int)((r + m) * 255), (int)((gg + m) * 255), (int)((b + m) * 255));
        }

        public static Color Lighten(Color c, float amount, int alpha) =>
            Color.FromArgb(alpha, (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));

        public static Color Darken(Color c, float amount, int alpha) =>
            Color.FromArgb(alpha, (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));
    }
}
