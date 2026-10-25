using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DeskMeters
{
    /// <summary>Small vector icons for meter headers and the tray.</summary>
    internal static class Glyphs
    {
        public static void Chip(Graphics g, float x, float y, float s, Color c)
        {
            using (var pen = new Pen(c, s / 11f))
            using (var fill = new SolidBrush(Color.FromArgb(60, c)))
            {
                float inset = s * 0.22f;
                var body = new RectangleF(x + inset, y + inset, s - inset * 2, s - inset * 2);
                using (GraphicsPath p = Theme.Rounded(body, s * 0.08f))
                {
                    g.FillPath(fill, p);
                    g.DrawPath(pen, p);
                }
                for (int i = 0; i < 3; i++)
                {
                    float t = body.X + body.Width * (i + 1) / 4f;
                    g.DrawLine(pen, t, y + s * 0.06f, t, body.Top);
                    g.DrawLine(pen, t, body.Bottom, t, y + s * 0.94f);
                    float u = body.Y + body.Height * (i + 1) / 4f;
                    g.DrawLine(pen, x + s * 0.06f, u, body.Left, u);
                    g.DrawLine(pen, body.Right, u, x + s * 0.94f, u);
                }
                var core = RectangleF.Inflate(body, -body.Width * 0.28f, -body.Height * 0.28f);
                using (var cb = new SolidBrush(c))
                    g.FillRectangle(cb, core);
            }
        }

        public static void Globe(Graphics g, float x, float y, float s, Color c)
        {
            using (var pen = new Pen(c, s / 12f))
            {
                var r = new RectangleF(x + s * 0.08f, y + s * 0.08f, s * 0.84f, s * 0.84f);
                using (var fill = new SolidBrush(Color.FromArgb(50, c)))
                    g.FillEllipse(fill, r);
                g.DrawEllipse(pen, r);
                g.DrawEllipse(pen, r.X + r.Width * 0.27f, r.Y, r.Width * 0.46f, r.Height);
                g.DrawLine(pen, r.X, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
                g.DrawLine(pen, r.X + r.Width / 2, r.Y, r.X + r.Width / 2, r.Bottom);
            }
        }

        public static void Drive(Graphics g, float x, float y, float s, Color c)
        {
            using (var pen = new Pen(c, s / 11f))
            using (var fill = new SolidBrush(Color.FromArgb(55, c)))
            {
                var body = new RectangleF(x + s * 0.06f, y + s * 0.24f, s * 0.88f, s * 0.52f);
                using (GraphicsPath p = Theme.Rounded(body, s * 0.1f))
                {
                    g.FillPath(fill, p);
                    g.DrawPath(pen, p);
                }
                using (var led = new SolidBrush(Theme.Green))
                    g.FillEllipse(led, body.Right - s * 0.24f, body.Y + body.Height / 2 - s * 0.07f, s * 0.14f, s * 0.14f);
                g.DrawLine(pen, body.X + s * 0.14f, body.Y + body.Height / 2, body.X + s * 0.5f, body.Y + body.Height / 2);
            }
        }

        public static void Card(Graphics g, float x, float y, float s, Color c)
        {
            using (var pen = new Pen(c, s / 11f))
            using (var fill = new SolidBrush(Color.FromArgb(55, c)))
            {
                var body = new RectangleF(x + s * 0.04f, y + s * 0.2f, s * 0.92f, s * 0.5f);
                using (GraphicsPath p = Theme.Rounded(body, s * 0.07f))
                {
                    g.FillPath(fill, p);
                    g.DrawPath(pen, p);
                }
                float fr = body.Height * 0.36f;
                g.DrawEllipse(pen, body.X + body.Width * 0.34f - fr, body.Y + body.Height / 2 - fr, fr * 2, fr * 2);
                g.DrawEllipse(pen, body.X + body.Width * 0.7f - fr, body.Y + body.Height / 2 - fr, fr * 2, fr * 2);
                // PCIe fingers
                for (int i = 0; i < 4; i++)
                {
                    float fx = body.X + body.Width * (0.18f + i * 0.12f);
                    g.DrawLine(pen, fx, body.Bottom, fx, body.Bottom + s * 0.14f);
                }
            }
        }

        /// <summary>Tray / window icon: three rising meter bars on a dark rounded tile.</summary>
        public static Icon AppIcon(int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float s = size;
                    using (GraphicsPath tile = Theme.Rounded(new RectangleF(0.5f, 0.5f, s - 1, s - 1), s * 0.2f))
                    using (var b = new LinearGradientBrush(new PointF(0, 0), new PointF(0, s), Color.FromArgb(255, 52, 60, 74), Color.FromArgb(255, 22, 26, 32)))
                        g.FillPath(b, tile);

                    Color[] colors = { Theme.Green, Theme.Yellow, Theme.Cyan };
                    float[] heights = { 0.38f, 0.58f, 0.78f };
                    float bw = s * 0.18f, gap = s * 0.08f, x0 = (s - (bw * 3 + gap * 2)) / 2;
                    for (int i = 0; i < 3; i++)
                    {
                        float h = s * heights[i] * 0.8f;
                        var r = new RectangleF(x0 + i * (bw + gap), s * 0.86f - h, bw, h);
                        using (GraphicsPath p = Theme.Rounded(r, bw * 0.25f))
                        using (var fb = new SolidBrush(colors[i]))
                            g.FillPath(fb, p);
                    }
                }

                IntPtr h2 = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h2))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(h2);
                }
            }
        }
    }
}
