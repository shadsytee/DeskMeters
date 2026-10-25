using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeskMeters
{
    internal enum ZMode { Desktop, Normal, Topmost }

    /// <summary>
    /// A borderless, per-pixel transparent window (layered, drawn with UpdateLayeredWindow).
    /// Handles dragging, edge snapping, z-order modes, DPI, clickable links and the
    /// shared right-click menu. Subclasses only lay out and draw their content.
    /// </summary>
    internal abstract class MeterWindow : Form
    {
        private readonly List<(RectangleF rect, Action click)> _hotspots = new List<(RectangleF, Action)>();
        private int _hover = -1;
        private RectangleF _lastHoverRect;
        private Point _downAt;         // screen position of the left-button press
        private Point _grabOffset;     // cursor minus window position at the press
        private bool _mouseDown;
        private bool _dragging;
        private bool _raised;          // temporarily above other windows (show desktop / tray double-click)
        private float _scale = 1f;
        private Skin _skin = Skins.Get(Skins.DefaultId);
        private bool _clickThrough;

        /// <summary>Window size in device pixels as of the last render.</summary>
        public Size PixelSize { get; private set; }

        /// <summary>True until the user moves this meter (default placement may be redone).</summary>
        public bool Provisional { get; set; }

        /// <summary>
        /// Shown away from its saved position because that monitor isn't available. The saved
        /// position is kept and restored when the monitor returns, unless the user moves it.
        /// </summary>
        public bool Displaced { get; set; }

        protected readonly MeterApp App;
        protected Snapshot Data;

        public abstract string Key { get; }
        public abstract string Title { get; }

        protected MeterWindow(MeterApp app)
        {
            App = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "DeskMeters";
            AutoScaleMode = AutoScaleMode.None;
        }

        // ---- settings -------------------------------------------------------------------

        protected Settings S => App.Settings;

        public int SizePct
        {
            get => Math.Max(60, Math.Min(250, S.GetInt(Key + ".size", 100)));
            set { S.SetInt(Key + ".size", value); Render(); }
        }

        public int OpacityPct
        {
            get => Math.Max(20, Math.Min(100, S.GetInt(Key + ".opacity", 100)));
            set { S.SetInt(Key + ".opacity", value); Render(); }
        }

        public ZMode ZMode
        {
            get => Enum.TryParse(S.Get(Key + ".z"), out ZMode z) ? z : ZMode.Desktop;
            set { S.Set(Key + ".z", value.ToString()); ApplyZOrder(); }
        }

        /// <summary>Design id for this meter, or null to follow the global one.</summary>
        public string DesignOverride
        {
            get => S.Get(Key + ".design");
            set { S.Set(Key + ".design", value); Render(); }
        }

        public Skin CurrentSkin => Skins.Get(DesignOverride ?? App.DesignId, App.Compact);

        /// <summary>Panel width in device pixels for <paramref name="skin"/> at this meter's size.</summary>
        public int PanelWidthPx(Skin skin) =>
            (int)Math.Ceiling(skin.Width * (IsHandleCreated ? Native.DpiFor(Handle) : 96) / 96f * SizePct / 100f);

        public bool HasSavedPosition => S.Has(Key + ".x") && S.Has(Key + ".y");

        public void SavePosition()
        {
            S.SetInt(Key + ".x", Left);
            S.SetInt(Key + ".y", Top);
        }

        public void RestorePosition()
        {
            Location = new Point(S.GetInt(Key + ".x", 0), S.GetInt(Key + ".y", 0));
        }

        /// <summary>Shadow margin in device pixels (the panel sits this far inside the window).</summary>
        public int MarginPx => (int)Math.Round((_skin.Shadow ? Theme.ShadowMargin : 0) * _scale);

        /// <summary>The visible panel, without the shadow margin, in screen pixels.</summary>
        public Rectangle PanelBounds
        {
            get
            {
                Size sz = PixelSize.IsEmpty ? Size : PixelSize;
                int m = MarginPx;
                return new Rectangle(Left + m, Top + m, Math.Max(1, sz.Width - 2 * m), Math.Max(1, sz.Height - 2 * m));
            }
        }

        /// <summary>Move so the panel's top-left lands on <paramref name="panelTopLeft"/>.</summary>
        public void MovePanelTo(Point panelTopLeft)
        {
            int m = MarginPx;
            Location = new Point(panelTopLeft.X - m, panelTopLeft.Y - m);
        }

        // ---- window plumbing ------------------------------------------------------------

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.ExcludeFromPeek(Handle);
            ApplyClickThrough();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
            ApplyZOrder();
        }

        /// <summary>Let clicks fall through to whatever is behind (turned off from the tray).</summary>
        public void SetClickThrough(bool on)
        {
            _clickThrough = on;
            ApplyClickThrough();
        }

        private void ApplyClickThrough()
        {
            if (!IsHandleCreated) return;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            int want = _clickThrough ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
            if (want != ex) Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, want);
        }

        public void ApplyZOrder()
        {
            if (!IsHandleCreated) return;
            const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
            if (_raised || ZMode == ZMode.Topmost)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            }
            else
            {
                Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                if (ZMode == ZMode.Desktop)
                    Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, flags);
            }
        }

        /// <summary>Lift above everything (while the desktop is shown, or briefly on request).</summary>
        public void SetRaised(bool raised)
        {
            if (_raised == raised) return;
            _raised = raised;
            ApplyZOrder();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;

                case Native.WM_WINDOWPOSCHANGING:
                    if (ZMode == ZMode.Desktop && !_raised && m.LParam != IntPtr.Zero)
                    {
                        var pos = (Native.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(Native.WINDOWPOS));
                        if ((pos.flags & Native.SWP_NOZORDER) == 0)
                        {
                            pos.hwndInsertAfter = Native.HWND_BOTTOM;
                            Marshal.StructureToPtr(pos, m.LParam, false);
                        }
                    }
                    break;

                case Native.WM_DPICHANGED:
                    // We size ourselves; just redraw at the new scale.
                    Render();
                    m.Result = IntPtr.Zero;
                    return;
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// Nudge a proposed window rectangle so its panel lines up with screen edges and
        /// with the other meters' panels (shadows don't count).
        /// </summary>
        private void Snap(ref Native.RECT r)
        {
            int dpi = Native.DpiFor(Handle);
            int threshold = (int)Math.Round(10 * dpi / 96.0);
            int gap = (int)Math.Round(8 * dpi / 96.0);
            int m = MarginPx;
            var p = Rectangle.FromLTRB(r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);

            var lefts = new List<int>();
            var rights = new List<int>();
            var tops = new List<int>();
            var bottoms = new List<int>();

            Rectangle wa = Screen.FromRectangle(p).WorkingArea;
            lefts.Add(wa.Left + gap); rights.Add(wa.Right - gap); tops.Add(wa.Top + gap); bottoms.Add(wa.Bottom - gap);
            lefts.Add(wa.Left); rights.Add(wa.Right); tops.Add(wa.Top); bottoms.Add(wa.Bottom);

            foreach (MeterWindow other in App.VisibleMeters)
            {
                if (other == this) continue;
                Rectangle o = other.PanelBounds;
                lefts.Add(o.Left); lefts.Add(o.Right + gap);
                rights.Add(o.Right); rights.Add(o.Left - gap);
                tops.Add(o.Top); tops.Add(o.Bottom + gap);
                bottoms.Add(o.Bottom); bottoms.Add(o.Top - gap);
            }

            int dx = Best(p.Left, lefts, threshold, out bool hx);
            int dx2 = Best(p.Right, rights, threshold, out bool hx2);
            if (hx2 && (!hx || Math.Abs(dx2) < Math.Abs(dx))) dx = dx2;
            else if (!hx) dx = 0;

            int dy = Best(p.Top, tops, threshold, out bool hy);
            int dy2 = Best(p.Bottom, bottoms, threshold, out bool hy2);
            if (hy2 && (!hy || Math.Abs(dy2) < Math.Abs(dy))) dy = dy2;
            else if (!hy) dy = 0;

            r.Left += dx; r.Right += dx;
            r.Top += dy; r.Bottom += dy;
        }

        private static int Best(int value, List<int> candidates, int threshold, out bool hit)
        {
            hit = false;
            int best = 0;
            foreach (int c in candidates)
            {
                int d = c - value;
                if (Math.Abs(d) <= threshold && (!hit || Math.Abs(d) < Math.Abs(best)))
                {
                    best = d;
                    hit = true;
                }
            }
            return best;
        }

        // ---- drawing --------------------------------------------------------------------

        public void Feed(Snapshot data)
        {
            Data = data;
            OnData(data);
            if (Visible) Render();
        }

        /// <summary>Called on every snapshot (even while hidden) so histories stay continuous.</summary>
        protected virtual void OnData(Snapshot data) { }

        /// <summary>
        /// Draw the content in logical (96 dpi, 100 %) pixels and return the height used.
        /// Called twice per frame: once on a probe surface to measure, then for real
        /// on top of the panel, so layout lives in exactly one place.
        /// </summary>
        protected abstract float Draw(Graphics g, float w);

        private float SafeDraw(Graphics g, float w)
        {
            try
            {
                return Draw(g, w);
            }
            catch (Exception ex)
            {
                Log.Error(Key + ".Draw", ex);
                return 60;
            }
        }

        public void Render()
        {
            if (!IsHandleCreated || IsDisposed) return;

            _skin = CurrentSkin;
            Theme.Use(_skin);
            _scale = Native.DpiFor(Handle) / 96f * SizePct / 100f;
            float lw = Theme.Width;
            float lh;
            float m = Theme.Margin;

            using (var probe = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                Theme.Prepare(pg);
                pg.ScaleTransform(_scale, _scale);
                _hotspots.Clear();
                lh = (float)Math.Ceiling(Math.Max(40, SafeDraw(pg, lw)));
            }

            int pw = (int)Math.Ceiling((lw + m * 2) * _scale);
            int ph = (int)Math.Ceiling((lh + m * 2) * _scale);

            // Keep WinForms' idea of our size in step with the layered surface, or its next
            // Location= would restore a stale size through SetWindowPos.
            if (Width != pw || Height != ph)
                Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, pw, ph,
                                    Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

            _hotspots.Clear();
            using (var bmp = new Bitmap(pw, ph, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    Theme.Prepare(g);
                    g.Clear(Color.Transparent);
                    g.ScaleTransform(_scale, _scale);
                    g.TranslateTransform(m, m);
                    Theme.Panel(g, lw, lh);
                    SafeDraw(g, lw);
                }

                Push(bmp);
            }

            PixelSize = new Size(pw, ph);
        }

        private void Push(Bitmap bmp)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0)); // premultiplied 32-bit DIB
                old = Native.SelectObject(mem, hbmp);

                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = (byte)Math.Round(255 * OpacityPct / 100.0),
                    AlphaFormat = Native.AC_SRC_ALPHA,
                };

                // pptDst = null keeps the current position, so a redraw mid-drag doesn't fight the move.
                Native.UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(mem, old);
                if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // ---- shared layout pieces -------------------------------------------------------

        /// <summary>
        /// The meter header in the current design: icon + title + value for most designs,
        /// a huge number for the HUD, LCD and Floating designs, an inverse-video tag for
        /// Terminal. <paramref name="side"/> lines sit next to a big number. Returns the y below.
        /// </summary>
        protected float Header(Graphics g, float w, Action<Graphics, float, float, float, Color> glyph,
                               string title, string value, Color valueColor, string subtitle = null, params string[] side)
        {
            Skin s = Theme.Skin;
            float pad = Theme.Pad;

            if (s.PanelStyle == PanelStyle.Clipped || s.PanelStyle == PanelStyle.Bezel || s.PanelStyle == PanelStyle.None)
                return BigHeader(g, w, title, value, valueColor, subtitle, side);

            if (s.Monospace && s.BarStyle == BarStyle.Blocks)
            {
                // Terminal: inverse-video tag
                float y = pad;
                string tag = " " + title.ToUpperInvariant() + " ";
                float tw = Theme.Measure(g, tag, Theme.BodyBold);
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, pad, y, tw, Theme.Row);
                Theme.Text(g, tag, Theme.BodyBold, Theme.PanelTop, pad, y, tw + 2, Theme.Row);
                if (value != null)
                    Theme.Text(g, value, Theme.BodyBold, valueColor, pad, y, w - pad * 2, Theme.Row, StringAlignment.Far);
                y += Theme.Row + 1;
                if (!string.IsNullOrEmpty(subtitle))
                {
                    Theme.Text(g, subtitle, Theme.Small, Theme.Dim, pad, y, w - pad * 2, Theme.Row);
                    y += Theme.Row;
                }
                return y + 2;
            }

            // Classic Refined: icon, title and value, a hairline, then the subtitle.
            if (s.HeaderRule)
            {
                float top = HeaderTop;
                const float hh = 18;
                float icon = s.IconPx;
                glyph(g, pad, top + (hh - icon) / 2, icon, Theme.Accent);
                float tx = pad + icon + (s.Compact ? 5 : 7);
                float valueW = value != null ? Theme.Measure(g, value, Theme.HeaderValue) + 6 : 0;
                Theme.Text(g, title, Theme.Header, Theme.Title, tx, top, w - pad - tx - valueW, hh);
                if (value != null)
                    Theme.Text(g, value, Theme.HeaderValue, valueColor, pad, top, w - pad * 2, hh, StringAlignment.Far);
                float y = top + hh + Theme.Gap;
                Theme.Divider(g, y, w);
                y += 1 + Theme.Gap;
                if (!string.IsNullOrEmpty(subtitle))
                {
                    Theme.Text(g, subtitle, Theme.Small, Theme.Dim, pad, y, w - pad * 2, 12);
                    y += 12 + Theme.Gap;
                }
                return y;
            }

            // Icon + title (+ subtitle under it) + value on the right
            {
                float top = Theme.K(6);
                bool light = s.PanelStyle == PanelStyle.Light;
                if (light)
                {
                    var badge = new RectangleF(pad, top + 1, Theme.K(22), Theme.K(22));
                    Theme.Fill(g, badge, Theme.K(6), Color.FromArgb(28, Theme.Accent));
                    float gi = Theme.K(14);
                    glyph(g, badge.X + (badge.Width - gi) / 2, badge.Y + (badge.Height - gi) / 2, gi, Theme.Accent);
                }
                else
                {
                    glyph(g, pad - 1, top + 1, Theme.K(15), Theme.Accent);
                }

                float th = Theme.K(17);
                float tx = light ? pad + Theme.K(29) : pad + Theme.K(19);
                float valueW = value != null ? Theme.Measure(g, value, Theme.HeaderValue) + 6 : 0;
                Theme.Text(g, title, Theme.Header, Theme.Title, tx, top, w - pad - tx - valueW, th);
                if (value != null)
                    Theme.Text(g, value, Theme.HeaderValue, valueColor, pad, top - 1, w - pad * 2, th + 1, StringAlignment.Far);

                float y = top + th + Theme.K(3);
                if (!string.IsNullOrEmpty(subtitle))
                {
                    float sx = light ? tx : pad;
                    Theme.Text(g, subtitle, Theme.Small, Theme.Dim, sx, light ? y - 5 : y - 2, w - pad - sx, Theme.Row);
                    y += Theme.Row - (light ? 3 : 1);
                }
                return y;
            }
        }

        /// <summary>Top of the header row in the Classic Refined layout.</summary>
        protected static float HeaderTop => Theme.Compact ? Theme.Pad - 2 : Theme.Pad - 1;

        private static float BigHeader(Graphics g, float w, string title, string value, Color valueColor, string subtitle, string[] side)
        {
            Skin s = Theme.Skin;
            float pad = Theme.Pad;
            bool floating = s.PanelStyle == PanelStyle.None;
            float y = pad - 2;

            // Caption row: "// CPU" (or just the title) left, subtitle right if it fits, else trimmed after it.
            string caption = s.PanelStyle == PanelStyle.Clipped ? "// " + title : title;
            Theme.Text(g, caption, floating ? Theme.Caption : Theme.Small, Theme.Accent, pad, y, w - pad * 2, Theme.Row);
            if (!string.IsNullOrEmpty(subtitle) && !floating)
            {
                float capW = Theme.Measure(g, caption, Theme.Small) + 8;
                float subW = w - pad * 2 - capW;
                if (Theme.Measure(g, subtitle, Theme.Small) <= subW)
                    Theme.Text(g, subtitle, Theme.Small, Theme.Dim, pad, y, w - pad * 2, Theme.Row, StringAlignment.Far);
                else
                    Theme.Text(g, subtitle, Theme.Small, Theme.Dim, pad + capW, y, subW, Theme.Row);
            }
            y += Theme.Row + 2;

            // Big number, shrunk if it would run into the side lines.
            float bigH = Theme.K(34);
            float sideW = 0;
            if (side != null)
                foreach (string line in side)
                    if (!string.IsNullOrEmpty(line)) sideW = Math.Max(sideW, Theme.Measure(g, line, Theme.Small));
            float maxW = w - pad * 2 - sideW - 8;
            Font big = floating ? Fonts.Get(s.FontFamily, s.HugePx * s.FontScale) : Theme.Huge;
            if (Theme.Measure(g, value ?? "--", big) > maxW) big = Theme.Big;
            if (Theme.Measure(g, value ?? "--", big) > maxW) big = Theme.HeaderValue;
            Theme.Text(g, value ?? "--", big, floating ? Theme.Title : valueColor, pad, y - 4, maxW + 4, bigH + 4);

            float sy = y + 1;
            if (side != null)
                foreach (string line in side)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    Theme.Text(g, line, Theme.Small, Theme.Fore, pad, sy, w - pad * 2, Theme.Row, StringAlignment.Far);
                    sy += Theme.Row - 1;
                }
            y += bigH;

            if (floating && !string.IsNullOrEmpty(subtitle))
            {
                // Below the side lines if they reach this far, so the two never overlap.
                float ty = Math.Max(y - 4, sy);
                Theme.Text(g, subtitle, Theme.Small, Theme.Dim, pad, ty, w - pad * 2, Theme.Row);
                y = ty + Theme.Row + 1;
                sy = y;
            }
            return Math.Max(y, sy) + 3;
        }

        /// <summary>Dim label on the left, coloured value on the right.</summary>
        protected static float Pair(Graphics g, float w, float y, string label, string value, Color valueColor)
        {
            Theme.Text(g, label, Theme.Body, Theme.Dim, Theme.Pad, y, w / 2);
            Theme.Text(g, value, Theme.Compact ? Theme.BodyBold : Theme.Body, valueColor, Theme.Pad, y, w - Theme.Pad * 2, Theme.Row, StringAlignment.Far);
            return y + Theme.Row;
        }

        /// <summary>Row of equal stat tiles (Light design). Returns the y below.</summary>
        protected static float Tiles(Graphics g, float w, float y, params (string caption, string value, Color color, string sub)[] tiles)
        {
            int n = tiles.Length;
            bool sub = false;
            foreach (var t in tiles) if (!string.IsNullOrEmpty(t.sub)) sub = true;
            float h = Theme.TileHeight(sub);
            float gap = Theme.K(5);
            float tw = (w - Theme.Pad * 2 - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
                Theme.Tile(g, new RectangleF(Theme.Pad + i * (tw + gap), y, tw, h), tiles[i].caption, tiles[i].value, tiles[i].color, tiles[i].sub);
            return y + h + gap;
        }

        /// <summary>Clickable one-line notice (orange), e.g. "Run as admin for temperatures".</summary>
        protected float Notice(Graphics g, float w, float y, string text, Action click)
        {
            if (string.IsNullOrEmpty(text)) return y;
            if (click != null)
                Link(g, text, w / 2, y, Theme.Orange, click, StringAlignment.Center, w - Theme.Pad * 2);
            else
                Theme.Text(g, text, Theme.Small, Theme.Orange, Theme.Pad, y, w - Theme.Pad * 2, Theme.Row, StringAlignment.Center);
            return y + Theme.Row + 2;
        }

        // ---- links ----------------------------------------------------------------------

        protected bool IsHovered(RectangleF r) => !_lastHoverRect.IsEmpty && _lastHoverRect == r;

        /// <summary>Draw clickable text and register it. Returns its width.</summary>
        protected float Link(Graphics g, string text, float x, float y, Color color, Action click, StringAlignment align = StringAlignment.Near, float maxWidth = 0, bool bold = false)
        {
            Skin s = Theme.Skin;
            if (s.Monospace && s.BarStyle == BarStyle.Blocks && !text.StartsWith("[", StringComparison.Ordinal))
                text = "[" + text.ToLowerInvariant() + "]";
            Font font = bold ? Theme.LinkBold : Theme.LinkFont;
            float tw = Theme.Measure(g, text, font) + 1;
            if (maxWidth > 0) tw = Math.Min(tw, maxWidth);
            float lx = align == StringAlignment.Far ? x - tw : align == StringAlignment.Center ? x - tw / 2 : x;
            var rect = new RectangleF(lx, y, tw, Theme.Row);
            bool hover = IsHovered(rect);
            _hotspots.Add((rect, click));
            Theme.Text(g, text, hover ? (bold ? Theme.LinkBoldHover : Theme.LinkHover) : font, hover ? Theme.Lighten(color, 0.3f, 255) : color, lx, y, tw + 2);
            return tw;
        }

        /// <summary>Pill button (Light design). Returns its width.</summary>
        protected float Button(Graphics g, string text, float x, float y, Color color, Action click, float width = 0)
        {
            float tw = width > 0 ? width : Theme.Measure(g, text, Theme.SmallBold) + 16;
            var r = new RectangleF(x, y, tw, ButtonHeight);
            bool hover = IsHovered(r);
            Theme.Fill(g, r, ButtonHeight / 2, hover ? Color.FromArgb(70, color) : Color.FromArgb(28, color));
            Theme.Text(g, text, Theme.SmallBold, color, r.X, r.Y, r.Width, r.Height, StringAlignment.Center);
            _hotspots.Add((r, click));
            return tw;
        }

        protected static float ButtonHeight => Theme.K(16);

        protected void Hotspot(RectangleF rect, Action click) => _hotspots.Add((rect, click));

        private int HitTest(Point client)
        {
            if (_scale <= 0) return -1;
            float m = _skin.Shadow ? Theme.ShadowMargin : 0;
            var p = new PointF(client.X / _scale - m, client.Y / _scale - m);
            for (int i = _hotspots.Count - 1; i >= 0; i--)
                if (_hotspots[i].rect.Contains(p)) return i;
            return -1;
        }

        // Dragging is done by hand rather than with the system move loop
        // (WM_NCLBUTTONDOWN/HTCAPTION): Windows doesn't move a WS_EX_NOACTIVATE window
        // live during that loop, so the meter would only jump into place on release.

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _mouseDown = true;
                _dragging = false;
                _downAt = MousePosition;
                _grabOffset = new Point(_downAt.X - Left, _downAt.Y - Top);
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_mouseDown && (MouseButtons & MouseButtons.Left) != 0 && !App.Locked)
            {
                Point now = MousePosition;
                if (!_dragging)
                {
                    Size slop = SystemInformation.DragSize;
                    if (Math.Abs(now.X - _downAt.X) > slop.Width || Math.Abs(now.Y - _downAt.Y) > slop.Height)
                    {
                        _dragging = true;
                        Provisional = false;
                        Displaced = false;
                        if (_hover != -1)
                        {
                            _hover = -1;
                            _lastHoverRect = RectangleF.Empty;
                            Render();
                        }
                        Cursor = Cursors.SizeAll;
                    }
                }

                if (_dragging)
                {
                    DragTo(now);
                    return;
                }
            }

            int hit = HitTest(e.Location);
            if (hit != _hover)
            {
                _hover = hit;
                _lastHoverRect = hit >= 0 ? _hotspots[hit].rect : RectangleF.Empty;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
                Render();
            }
        }

        private void DragTo(Point cursor)
        {
            Size sz = PixelSize.IsEmpty ? Size : PixelSize;
            var r = new Native.RECT
            {
                Left = cursor.X - _grabOffset.X,
                Top = cursor.Y - _grabOffset.Y,
            };
            r.Right = r.Left + sz.Width;
            r.Bottom = r.Top + sz.Height;

            if (App.SnapEnabled && (ModifierKeys & Keys.Shift) == 0)
                Snap(ref r);

            if (r.Left != Left || r.Top != Top)
                Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, 0, 0,
                                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            _mouseDown = false;
            Cursor = Cursors.Default;
            SavePosition();
            App.Settings.Save();
            ApplyZOrder();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) EndDrag();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1 && !_dragging)
            {
                _hover = -1;
                _lastHoverRect = RectangleF.Empty;
                Cursor = Cursors.Default;
                Render();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                if (_dragging)
                {
                    EndDrag();
                    Capture = false;
                    return;
                }

                if (_mouseDown)
                {
                    _mouseDown = false;
                    Capture = false;
                    int hit = HitTest(e.Location);
                    if (hit >= 0)
                    {
                        try { _hotspots[hit].click(); }
                        catch (Exception ex) { Log.Error(Key + " click", ex); }
                    }
                }
            }
            else if (e.Button == MouseButtons.Right && !_dragging)
            {
                ShowMenu(PointToScreen(e.Location));
            }
        }

        // ---- context menu ---------------------------------------------------------------

        protected virtual void AddMenuItems(ToolStripItemCollection items) { }

        private void ShowMenu(Point screen)
        {
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
            ToolStripItemCollection items = menu.Items;

            items.Add(new ToolStripLabel(Title + " meter") { ForeColor = Color.FromArgb(138, 150, 166), Font = new Font(menu.Font, FontStyle.Bold) });
            items.Add(new ToolStripSeparator());

            int before = items.Count;
            AddMenuItems(items);
            if (items.Count > before) items.Add(new ToolStripSeparator());

            var design = new ToolStripMenuItem("Design for this meter");
            string current = DesignOverride;
            design.DropDownItems.Add(Check("Same as the others (" + Skins.Get(App.DesignId).Name + ")", current == null, () => DesignOverride = null));
            design.DropDownItems.Add(new ToolStripSeparator());
            foreach (Skin sk in Skins.All)
            {
                string id = sk.Id;
                design.DropDownItems.Add(Check(sk.Name, current == id, () => DesignOverride = id));
            }
            items.Add(design);

            var place = new ToolStripMenuItem("Position");
            place.DropDownItems.Add(Check("On the desktop (behind windows)", ZMode == ZMode.Desktop, () => ZMode = ZMode.Desktop));
            place.DropDownItems.Add(Check("Normal window", ZMode == ZMode.Normal, () => ZMode = ZMode.Normal));
            place.DropDownItems.Add(Check("Always on top", ZMode == ZMode.Topmost, () => ZMode = ZMode.Topmost));
            items.Add(place);

            var size = new ToolStripMenuItem("Size");
            foreach (int pct in new[] { 70, 80, 90, 100, 110, 125, 150, 175, 200 })
            {
                int p = pct;
                size.DropDownItems.Add(Check(p + "%", SizePct == p, () => SizePct = p));
            }
            items.Add(size);

            var opacity = new ToolStripMenuItem("Opacity");
            foreach (int pct in new[] { 100, 90, 80, 70, 60, 50, 40, 30 })
            {
                int p = pct;
                opacity.DropDownItems.Add(Check(p + "%", OpacityPct == p, () => OpacityPct = p));
            }
            items.Add(opacity);

            items.Add(Check("Compact layout (all meters)", App.Compact, () => App.Compact = !App.Compact));
            items.Add(Check("Apply position, size and opacity to all meters", false, () => App.CopyLookToAll(this)));
            items.Add(new ToolStripSeparator());
            items.Add(Check("Lock positions", App.Locked, () => App.Locked = !App.Locked));
            items.Add(Check("Hide this meter", false, () => App.SetMeterVisible(this, false)));
            items.Add(new ToolStripSeparator());
            items.Add(Check("All settings…", false, () => App.ShowTrayMenu()));
            items.Add(Check("Exit DeskMeters", false, App.Exit));

            menu.Closed += (s, e) => BeginInvoke(new Action(ApplyZOrder));
            App.ShowMenu(menu, screen);
        }

        public static ToolStripMenuItem Check(string text, bool isChecked, Action click)
        {
            var item = new ToolStripMenuItem(text) { Checked = isChecked };
            item.Click += (s, e) =>
            {
                try { click(); }
                catch (Exception ex) { Log.Error("Menu " + text, ex); }
            };
            return item;
        }
    }

    /// <summary>Dark context menus to match the meters.</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Back = Color.FromArgb(32, 36, 43);
        private static readonly Color Hover = Color.FromArgb(56, 64, 78);
        private static readonly Color Line = Color.FromArgb(70, 78, 92);
        private static readonly Color TextColor = Color.FromArgb(214, 221, 230);
        private static readonly Color Disabled = Color.FromArgb(110, 120, 135);
        private static readonly Color Tick = Color.FromArgb(120, 214, 140);

        public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!(e.Item is ToolStripLabel))
                e.TextColor = e.Item.Enabled ? TextColor : Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Disabled;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            Graphics g = e.Graphics;
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(Tick, 2f))
            {
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Min(r.Width, r.Height) / 2.6f;
                g.DrawLines(pen, new[]
                {
                    new PointF(cx - s, cy),
                    new PointF(cx - s / 3f, cy + s * 0.7f),
                    new PointF(cx + s, cy - s * 0.7f),
                });
            }
            g.SmoothingMode = old;
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Back;
            public override Color CheckBackground => Back;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }
    }
}
