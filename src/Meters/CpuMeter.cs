using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DeskMeters.Meters
{
    internal sealed class CpuMeter : MeterWindow
    {
        private readonly Ring _total = new Ring(200);
        private readonly List<Ring> _rows = new List<Ring>();
        private float[] _peaks = new float[0];
        private bool _rowsPerCore;

        public CpuMeter(MeterApp app) : base(app) { }

        public override string Key => "cpu";
        public override string Title => "CPU";

        /// <summary>Row display: "auto" follows the design, otherwise a <see cref="RowMode"/> name.</summary>
        private string RowSetting
        {
            get => S.Get("cpu.rows", "auto");
            set { S.Set("cpu.rows", value); Render(); }
        }

        private RowMode Rows
        {
            get
            {
                string v = RowSetting;
                // Settings from the first version used these names.
                if (v == "threads") return RowMode.List;
                if (v == "threads2") return RowMode.TwoColumns;
                if (v == "cores") return RowMode.Cores;
                if (v == "off") return RowMode.Hidden;
                return Enum.TryParse(v, out RowMode m) ? m : CurrentSkin.DefaultRows;
            }
        }

        private bool ShowGraph
        {
            get => S.GetBool("cpu.graph", true);
            set { S.SetBool("cpu.graph", value); Render(); }
        }

        private bool ShowMemory
        {
            get => S.GetBool("cpu.memory", true);
            set { S.SetBool("cpu.memory", value); Render(); }
        }

        protected override void OnData(Snapshot data)
        {
            CpuSnap cpu = data.Cpu;
            if (cpu?.ThreadLoad == null) return;

            _total.Push(cpu.Total);

            bool perCore = Rows == RowMode.Cores;
            float[] values = RowValues(cpu, perCore, out _);
            if (perCore != _rowsPerCore || _rows.Count != values.Length)
            {
                _rowsPerCore = perCore;
                _rows.Clear();
                for (int i = 0; i < values.Length; i++) _rows.Add(new Ring(100));
                _peaks = new float[values.Length];
            }
            for (int i = 0; i < values.Length; i++)
            {
                _rows[i].Push(values[i]);
                _peaks[i] = Math.Max(values[i], _peaks[i] - 2.5f);
            }
        }

        /// <summary>Per-row load (and temperature), per thread or per physical core.</summary>
        private static float[] RowValues(CpuSnap cpu, bool perCore, out float?[] temps)
        {
            if (!perCore)
            {
                temps = cpu.ThreadTemp;
                return cpu.ThreadLoad;
            }

            int n = Math.Max(1, cpu.CoreCount);
            var load = new float[n];
            var count = new int[n];
            temps = new float?[n];
            for (int i = 0; i < cpu.ThreadLoad.Length; i++)
            {
                int c = Math.Min(n - 1, Math.Max(0, cpu.ThreadCore[i]));
                load[c] += cpu.ThreadLoad[i];
                count[c]++;
                if (cpu.ThreadTemp != null && cpu.ThreadTemp[i].HasValue) temps[c] = cpu.ThreadTemp[i];
            }
            for (int c = 0; c < n; c++)
                if (count[c] > 0) load[c] /= count[c];
            return load;
        }

        private static readonly Regex Noise = new Regex(@"\((R|TM)\)|\bCPU\b|@.*$|\bProcessor\b|\bwith Radeon.*$|^(AMD|Intel)\s+|\s{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name;
            for (int i = 0; i < 3; i++) s = Noise.Replace(s, " ").Trim();
            return s;
        }

        // ---- drawing -----------------------------------------------------------------------

        protected override float Draw(Graphics g, float w)
        {
            Skin skin = Theme.Skin;
            CpuSnap cpu = Data?.Cpu;
            bool ready = cpu?.ThreadLoad != null;

            if (skin.Layout == MeterLayout.Strip)
                return DrawStrip(g, w, cpu);

            string vendor = cpu == null || string.IsNullOrEmpty(cpu.Vendor) ? "" : cpu.Vendor + " ";
            string name = cpu == null ? "Loading sensors…" : vendor + ShortName(cpu.Name);
            string clock = ready && cpu.ClockMHz > 0 ? Fmt.MHz(cpu.ClockMHz) : null;
            string temp = ready && cpu.PackageTemp.HasValue ? Fmt.Temp(cpu.PackageTemp) : null;
            string power = ready && cpu.PackagePower.HasValue ? Fmt.Watts(cpu.PackagePower) : null;

            float y = Header(g, w, Glyphs.Chip, "CPU", ready ? Fmt.Pct(cpu.Total) : null, Theme.LoadColor(cpu?.Total ?? 0), name, clock, temp, power);
            if (!ready)
                return y + 8;

            float pad = Theme.Pad;
            float bw = w - pad * 2;

            if (skin.StatTiles)
            {
                y = Tiles(g, w, y + 1,
                          ("Clock", clock != null ? Fmt.Num(cpu.ClockMHz / 1000.0, "0.0") + (skin.Compact ? "GHz" : " GHz") : "--", Theme.Title, null),
                          ("Temp", temp ?? "--", temp != null ? Theme.TempColor(cpu.PackageTemp) : Theme.Faint, null),
                          ("Power", power ?? "--", power != null ? Theme.Title : Theme.Faint, null));
            }
            else if (!skin.BigHeader)
            {
                // "3399 MHz" on the left; "48°C · 62 W" on the right (temperature coloured).
                float rowH = skin.HeaderRule ? (skin.Compact ? 13 : 14) : Theme.Row;
                Font cf = skin.Compact ? Theme.BodyBold : Theme.Body;
                Theme.Text(g, clock ?? "", cf, Theme.Fore, pad, y, w / 2, rowH);
                string pw = power != null ? (temp != null ? (skin.Compact ? " · " : "  ·  ") : "") + power : "";
                float pwW = Theme.Measure(g, pw, cf);
                Theme.Text(g, pw, cf, Theme.Fore, pad, y, bw, rowH, StringAlignment.Far);
                if (temp != null)
                    Theme.Text(g, temp, Theme.BodyBold, Theme.TempColor(cpu.PackageTemp), pad, y, bw - pwW, rowH, StringAlignment.Far);
                if (skin.HeaderRule)
                {
                    y += rowH + Theme.Gap;
                    Theme.Divider(g, y, w);
                    y += 1 + Theme.Gap;
                }
                else
                {
                    y += Theme.Row + Theme.K(4);
                    Theme.Divider(g, y, w);
                    y += Theme.K(4);
                }
            }

            if (ShowMemory)
                y = skin.Layout == MeterLayout.Rings ? DrawMemoryRings(g, w, y, cpu) : DrawMemory(g, w, y, cpu);

            RowMode mode = Rows;
            if (mode != RowMode.Hidden && _rows.Count > 0)
            {
                if (ShowMemory || skin.StatTiles)
                {
                    if (skin.HeaderRule)
                    {
                        Theme.Divider(g, y, w);
                        y += 1 + Theme.Gap;
                    }
                    else
                    {
                        Theme.Divider(g, y + 1, w);
                        y += Theme.K(5);
                    }
                }
                y = DrawRows(g, w, y, cpu, mode);
            }

            if (ShowGraph)
            {
                y += skin.HeaderRule ? Theme.Gap : Theme.K(4);
                var series = new List<(Ring, Color, bool, float)>();
                // Per-thread lines are faint context; the total is the story.
                if (skin.GraphThreadLines && _rows.Count <= 64 && skin.GraphStyle == GraphStyle.Area)
                    for (int i = 0; i < _rows.Count; i++)
                        series.Add((_rows[i], Color.FromArgb(60, Theme.Series(i, _rows.Count)), false, 0.7f));
                series.Add((_total, Theme.Green, true, skin.HeaderRule ? 1.4f : 1.5f));
                float gh = skin.GraphStyle == GraphStyle.HeatStrip ? Theme.K(16) : Theme.G(skin.HeaderRule ? 50 : 46);
                Theme.Graph(g, new RectangleF(pad, y, bw, gh), 100, series.ToArray());
                y += gh + Theme.K(4);
            }

            y = DrawNotice(g, w, y + 2, cpu);
            return y + Theme.K(skin.HeaderRule ? 7 : 5);
        }

        private float DrawMemory(Graphics g, float w, float y, CpuSnap cpu)
        {
            Skin skin = Theme.Skin;
            float pad = Theme.Pad;
            float bw = w - pad * 2;
            ulong ramUsed = cpu.RamTotal > cpu.RamAvail ? cpu.RamTotal - cpu.RamAvail : 0;
            ulong pageFree = cpu.PageTotal > cpu.PageUsed ? cpu.PageTotal - cpu.PageUsed : 0;
            bool hasPage = cpu.PageTotal > 0;
            float ramPct = cpu.RamTotal > 0 ? 100f * ramUsed / cpu.RamTotal : 0;
            float pagePct = hasPage ? 100f * cpu.PageUsed / cpu.PageTotal : 0;

            bool table = skin.BarStyle == BarStyle.Gradient || skin.BarStyle == BarStyle.Segmented || skin.BarStyle == BarStyle.Flat;
            if (table)
            {
                float labelW = skin.Compact ? 28 : skin.HeaderRule ? 34 : 30;
                float colW = (bw - labelW) / 3f;
                float x0 = pad + labelW;
                string[] heads = { "Used", "Free", "Total" };
                for (int i = 0; i < 3; i++)
                    Theme.Text(g, heads[i], Theme.Small, Theme.Faint, x0 + colW * i, y, colW, Theme.Row, StringAlignment.Far);
                y += Theme.Row - 1;
                MemoryRow(g, y, x0, colW, labelW, "RAM", ramUsed, cpu.RamAvail, cpu.RamTotal);
                y += Theme.Row;
                if (hasPage)
                {
                    MemoryRow(g, y, x0, colW, labelW, "Page", cpu.PageUsed, pageFree, cpu.PageTotal);
                    y += Theme.Row;
                }
                float gap = skin.HeaderRule ? Theme.Gap : skin.Compact ? 2 : 3;
                y += gap;
                y += Theme.Bar(g, pad, y, bw, ramPct, Theme.FullnessColor(ramPct), "RAM", Fmt.Pct(ramPct)) + gap;
                if (hasPage)
                    y += Theme.Bar(g, pad, y, bw, pagePct, Theme.Purple, "Page file", Fmt.Pct(pagePct)) + gap;
                return y + (skin.HeaderRule ? 0 : 1);
            }

            // Label-carrying bars (Terminal, LCD, Floating): no table needed.
            y += Theme.Bar(g, pad, y, bw, ramPct, Theme.FullnessColor(ramPct), "RAM " + Fmt.Bytes(ramUsed) + "/" + Fmt.Bytes(cpu.RamTotal), Fmt.Pct(ramPct)) + 3;
            if (hasPage)
                y += Theme.Bar(g, pad, y, bw, pagePct, Theme.Purple, "Page " + Fmt.Bytes(cpu.PageUsed) + "/" + Fmt.Bytes(cpu.PageTotal), Fmt.Pct(pagePct)) + 3;
            return y + 1;
        }

        private static void MemoryRow(Graphics g, float y, float x0, float colW, float labelW, string label, ulong used, ulong free, ulong total)
        {
            Theme.Text(g, label, Theme.Small, Theme.Dim, Theme.Pad, y, labelW);
            Theme.Text(g, Fmt.Bytes(used), Theme.Small, Theme.Fore, x0, y, colW, Theme.Row, StringAlignment.Far);
            Theme.Text(g, Fmt.Bytes(free), Theme.Small, Theme.Fore, x0 + colW, y, colW, Theme.Row, StringAlignment.Far);
            Theme.Text(g, Fmt.Bytes(total), Theme.Small, Theme.Fore, x0 + colW * 2, y, colW, Theme.Row, StringAlignment.Far);
        }

        /// <summary>Rings design: temperature, RAM and page file as three small rings.</summary>
        private float DrawMemoryRings(Graphics g, float w, float y, CpuSnap cpu)
        {
            float pad = Theme.Pad;
            float colW = (w - pad * 2) / 3f;
            ulong ramUsed = cpu.RamTotal > cpu.RamAvail ? cpu.RamTotal - cpu.RamAvail : 0;
            float ramPct = cpu.RamTotal > 0 ? 100f * ramUsed / cpu.RamTotal : 0;
            float pagePct = cpu.PageTotal > 0 ? 100f * cpu.PageUsed / cpu.PageTotal : 0;
            float tempPct = cpu.PackageTemp.HasValue ? Fmt.Clamp(cpu.PackageTemp.Value, 0, 100) : 0;

            var items = new[]
            {
                (value: cpu.PackageTemp.HasValue ? Fmt.TempShort(cpu.PackageTemp) : "--", color: Theme.TempColor(cpu.PackageTemp), pct: tempPct, label: "Temp"),
                (value: Fmt.Pct(ramPct), color: Theme.FullnessColor(ramPct), pct: ramPct, label: "RAM"),
                (value: Fmt.Pct(pagePct), color: Theme.Purple, pct: pagePct, label: "Page"),
            };
            float r = Theme.K(20);
            float thick = Theme.Compact ? 4.5f : 5.5f;
            for (int i = 0; i < 3; i++)
            {
                float cx = pad + colW * i + colW / 2;
                Theme.Ring(g, cx, y + r + 3, r, thick, items[i].pct, items[i].color);
                Theme.Text(g, items[i].value, Theme.SmallBold, items[i].color, cx - r, y + 3 + r - 6, r * 2, 12, StringAlignment.Center);
                Theme.Text(g, items[i].label, Theme.Small, Theme.Dim, cx - colW / 2, y + r * 2 + 7, colW, 12, StringAlignment.Center);
            }
            return y + r * 2 + 7 + Theme.K(14);
        }

        private float DrawRows(Graphics g, float w, float y, CpuSnap cpu, RowMode mode)
        {
            switch (mode)
            {
                case RowMode.TwoColumns: return DrawRowsTwoColumns(g, w, y, cpu);
                case RowMode.Grid: return DrawGrid(g, w, y, cpu);
                case RowMode.Equalizer: return DrawEqualizer(g, w, y, cpu);
                case RowMode.Dial: return DrawDial(g, w, y, cpu);
                default: return DrawRowsList(g, w, y, cpu, mode == RowMode.Cores);
            }
        }

        private float DrawRowsList(Graphics g, float w, float y, CpuSnap cpu, bool perCore)
        {
            Skin skin = Theme.Skin;
            float[] loads = RowValues(cpu, perCore, out float?[] temps);
            bool anyTemp = temps != null && temps.Any(t => t.HasValue);
            bool compact = skin.Compact;
            float rowH = !skin.RowBars ? Theme.ThreadRow
                       : compact ? Math.Max(Theme.ThreadRow, Theme.BarHeight(false))
                       : Math.Max(12f, Theme.BarHeight(false) + 2);
            float pad = Theme.Pad;
            float gap = compact ? 3f : 4f;
            Font idxFont = compact ? Theme.SmallBold : Theme.Small;

            float idxW = skin.Monospace ? Theme.Measure(g, "00", Theme.Small) : compact ? 13 : 15;
            float pctW = skin.Monospace ? Theme.Measure(g, " 100%", Theme.Small) : compact ? 24 : 28;
            float tempW = anyTemp ? (skin.Monospace ? Theme.Measure(g, " 99°", Theme.Small) : compact ? 21 : 24) : 0;
            float barX = pad + idxW + gap;
            float barW = w - pad * 2 - idxW - pctW - (anyTemp ? tempW + gap : 0) - gap * 2;

            int n = Math.Min(loads.Length, _rows.Count);
            for (int i = 0; i < n; i++)
            {
                Color c = Theme.Series(i, n);
                string idx = skin.Monospace ? (i + 1).ToString("00") : (i + 1).ToString();
                Theme.Text(g, idx, idxFont, skin.Monospace ? Theme.Dim : c, pad, y, idxW, rowH, skin.Monospace ? StringAlignment.Near : StringAlignment.Far);

                if (skin.RowBars)
                    Theme.Bar(g, barX, y + (rowH - Theme.BarHeight(false)) / 2, barW, loads[i], skin.Monospace ? Theme.LoadColor(loads[i]) : c);
                else
                    Theme.Spark(g, new RectangleF(barX, y + 2, barW, rowH - 4), _rows[i], c);

                if (anyTemp)
                {
                    float? t = temps[i];
                    Color tc = skin.Monospace ? Theme.Dim : Color.FromArgb(compact ? 255 : 217, Theme.TempColor(t));
                    Theme.Text(g, t.HasValue ? Fmt.TempShort(t) : "", Theme.Small, tc, barX + barW + gap, y, tempW, rowH, StringAlignment.Far);
                }

                Theme.Text(g, Fmt.Pct(loads[i]), Theme.SmallBold, Theme.LoadColor(loads[i]), pad, y, w - pad * 2, rowH, StringAlignment.Far);
                y += rowH;
            }
            return y;
        }

        private float DrawRowsTwoColumns(Graphics g, float w, float y, CpuSnap cpu)
        {
            Skin skin = Theme.Skin;
            float[] loads = RowValues(cpu, false, out _);
            bool compact = skin.Compact;
            float rowH = Theme.ThreadRow;
            float gutter = compact ? 6f : 8f;
            float idxW = compact ? 13f : 15f, pctW = compact ? 22f : 27f;
            Font font = compact ? Theme.SmallBold : Theme.Small;
            float pad = Theme.Pad;
            int n = Math.Min(loads.Length, _rows.Count);
            int perCol = (n + 1) / 2;
            float colW = (w - pad * 2 - gutter) / 2f;

            for (int i = 0; i < n; i++)
            {
                int col = i / perCol, row = i % perCol;
                float x = pad + col * (colW + gutter);
                float ry = y + row * rowH;
                Color c = Theme.Series(i, n);
                Theme.Text(g, (i + 1).ToString(), font, c, x, ry, idxW, rowH);
                float bx = x + idxW, bwid = colW - idxW - pctW;
                if (skin.RowBars)
                {
                    if (skin.BarStyle == BarStyle.Blocks)
                        Theme.Bar(g, bx, ry, bwid, loads[i], Theme.LoadColor(loads[i]));
                    else
                    {
                        // Short rows: a slim bar in the skin's colours
                        float sh = compact ? 6f : 5f, sy = ry + (rowH - sh) / 2;
                        Theme.Fill(g, new RectangleF(bx, sy, bwid, sh), sh / 2, Theme.Track);
                        Theme.Fill(g, new RectangleF(bx, sy, Math.Max(2, bwid * loads[i] / 100f), sh), sh / 2, c);
                    }
                }
                else
                    Theme.Spark(g, new RectangleF(bx, ry + 1.5f, bwid, rowH - 3), _rows[i], c);
                Theme.Text(g, Fmt.Pct(loads[i]), font, Theme.LoadColor(loads[i]), x, ry, colW, rowH, StringAlignment.Far);
            }
            return y + perCol * rowH;
        }

        /// <summary>Heatmap: a grid of tiles coloured by load, temperature underneath.</summary>
        private float DrawGrid(Graphics g, float w, float y, CpuSnap cpu)
        {
            float[] loads = RowValues(cpu, false, out float?[] temps);
            bool anyTemp = temps != null && temps.Any(t => t.HasValue);
            float pad = Theme.Pad;
            int n = loads.Length;
            int cols = n > 16 ? 8 : n > 8 ? 6 : 4;
            float gap = Theme.Compact ? 2f : 3f;
            float cw = (w - pad * 2 - gap * (cols - 1)) / cols;
            float ch = Theme.K(anyTemp ? 26 : 20);
            float numH = Theme.K(13);
            Font numFont = Theme.Compact && cols >= 8 ? Theme.Caption : Theme.SmallBold;
            int rows = (n + cols - 1) / cols;
            bool light = Theme.Skin.PanelStyle == PanelStyle.Light;

            for (int i = 0; i < n; i++)
            {
                int cx = i % cols, cy = i / cols;
                var r = new RectangleF(pad + cx * (cw + gap), y + cy * (ch + gap), cw, ch);
                float l = loads[i];
                Color c = Theme.LoadColor(l);
                int alpha = (int)(45 + 190 * Math.Min(1f, l / 70f));
                Theme.Fill(g, r, 3.5f, Color.FromArgb(alpha, c));
                Theme.Outline(g, r, 3.5f, Color.FromArgb(18, 255, 255, 255));
                Color txt = light ? (l > 35 ? Color.White : Theme.Title) : Color.FromArgb(244, 247, 250);
                Theme.Text(g, Math.Round(l).ToString(), numFont, txt, r.X, r.Y + (anyTemp ? 1 : 0), r.Width, anyTemp ? numH : ch, StringAlignment.Center);
                if (anyTemp)
                    Theme.Text(g, temps[i].HasValue ? Fmt.TempShort(temps[i]) : "", Theme.Tiny, Color.FromArgb(Theme.Compact ? 200 : 170, txt), r.X, r.Y + numH, r.Width, ch - numH, StringAlignment.Center);
            }
            y += rows * (ch + gap) - gap + 3;
            Theme.Text(g, n + " threads · load %" + (anyTemp ? " / temp" : ""), Theme.Caption, Theme.Faint, pad, y, w - pad * 2, Theme.K(11));
            return y + Theme.K(12);
        }

        /// <summary>Equalizer: one vertical bar per thread with a peak cap.</summary>
        private float DrawEqualizer(Graphics g, float w, float y, CpuSnap cpu)
        {
            Skin skin = Theme.Skin;
            float[] loads = RowValues(cpu, false, out _);
            float pad = Theme.Pad;
            int n = Math.Min(loads.Length, _peaks.Length);
            if (n == 0) return y;
            float boxH = Theme.K(84);
            var box = new RectangleF(pad, y, w - pad * 2, boxH);
            bool lcd = skin.BarStyle == BarStyle.Lcd;
            if (!lcd) Theme.Fill(g, box, 5, Theme.GraphBack);
            float inner = box.Height - 8;
            const float gap = 1.5f;
            float bw = (box.Width - 8 - gap * (n - 1)) / n;
            float baseY = box.Bottom - 4;

            Color cutColor = Theme.PanelBottom.A > 200 ? Color.FromArgb(255, Theme.PanelBottom) : Color.FromArgb(255, 12, 14, 18);
            for (int i = 0; i < n; i++)
            {
                float x = box.X + 4 + i * (bw + gap);
                float v = Fmt.Clamp(loads[i], 0, 100) / 100f;
                if (lcd)
                {
                    const int segs = 10;
                    float segH = (inner - (segs - 1)) / segs;
                    int lit = (int)Math.Round(v * segs);
                    using (var on = new SolidBrush(Theme.Fore))
                    using (var off = new SolidBrush(Theme.Track))
                        for (int s = 0; s < segs; s++)
                            g.FillRectangle(s < lit ? on : off, x, baseY - (s + 1) * segH - s, bw, segH);
                    continue;
                }

                float h = Math.Max(v > 0.01f ? 2 : 1, inner * v);
                using (var brush = new LinearGradientBrush(new PointF(0, baseY), new PointF(0, baseY - inner), Theme.Green, Theme.Red))
                {
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Theme.Green, Theme.Green, Theme.Yellow, Theme.Orange, Theme.Red },
                        Positions = new[] { 0f, 0.4f, 0.65f, 0.82f, 1f },
                    };
                    g.FillRectangle(brush, x, baseY - h, bw, h);
                }
                using (var cut = new SolidBrush(cutColor))
                    for (float sy = baseY - 4; sy > baseY - h; sy -= 4)
                        g.FillRectangle(cut, x, sy, bw, 1);
                float peak = Fmt.Clamp(_peaks[i], 0, 100) / 100f * inner;
                if (peak > 3)
                    using (var pb = new SolidBrush(Color.FromArgb(200, Theme.Title)))
                        g.FillRectangle(pb, x, baseY - peak - 1, bw, 1.5f);
            }

            y += boxH + 2;
            float lh = Theme.K(10);
            Theme.Text(g, "1", Theme.Caption, Theme.Faint, box.X + 4, y, 20, lh);
            Theme.Text(g, n.ToString(), Theme.Caption, Theme.Faint, box.X, y, box.Width - 4, lh, StringAlignment.Far);
            if (n >= 8)
                Theme.Text(g, (n / 2).ToString(), Theme.Caption, Theme.Faint, box.X, y, box.Width, lh, StringAlignment.Center);
            return y + lh + 1;
        }

        /// <summary>Rings design: total load as a dial with a tick per thread around it.</summary>
        private float DrawDial(Graphics g, float w, float y, CpuSnap cpu)
        {
            float[] loads = RowValues(cpu, false, out _);
            float pad = Theme.Pad;
            float size = Theme.K(136);
            float cx = w / 2, cy = y + size / 2;
            int n = loads.Length;
            float r1 = Theme.K(46);
            float reach = Theme.K(17) / 100f;
            using (var pen = new Pen(Theme.Green, Theme.Compact ? 2f : 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                for (int i = 0; i < n; i++)
                {
                    double a = (-90 + i * 360.0 / n) * Math.PI / 180;
                    float r2 = r1 + 3 + Fmt.Clamp(loads[i], 0, 100) * reach;
                    pen.Color = Theme.LoadColor(loads[i]);
                    g.DrawLine(pen, cx + r1 * (float)Math.Cos(a), cy + r1 * (float)Math.Sin(a), cx + r2 * (float)Math.Cos(a), cy + r2 * (float)Math.Sin(a));
                }
            }
            Theme.Ring(g, cx, cy, Theme.K(34), Theme.K(8), cpu.Total, Theme.LoadColor(cpu.Total));
            float bh = Theme.K(24);
            Theme.Text(g, Fmt.Pct(cpu.Total), Theme.Big, Theme.Title, cx - 40, cy - bh * 0.67f, 80, bh, StringAlignment.Center);
            Theme.Text(g, cpu.ClockMHz > 0 ? Fmt.Num(cpu.ClockMHz / 1000.0, "0.00") + " GHz" : "", Theme.Small, Theme.Dim, cx - 40, cy + bh * 0.29f, 80, 12, StringAlignment.Center);
            y += size;
            Theme.Text(g, n + " threads around the dial", Theme.Caption, Theme.Faint, pad, y - 4, w - pad * 2, 11, StringAlignment.Center);
            return y + 8;
        }

        /// <summary>Taskbar Strip: one short, wide row.</summary>
        private float DrawStrip(Graphics g, float w, CpuSnap cpu)
        {
            float pad = Theme.Pad;
            float h = Theme.K(54);
            Glyphs.Chip(g, pad, Theme.K(10), Theme.K(14), Theme.Accent);
            Theme.Text(g, "CPU", Theme.Caption, Theme.Dim, pad + Theme.K(18), Theme.K(8), 60, 12);
            if (cpu?.ThreadLoad == null)
            {
                Theme.Text(g, "Loading sensors…", Theme.Body, Theme.Dim, pad + Theme.K(18), Theme.K(24), w - pad * 2 - 18, Theme.Row);
                return h;
            }
            Theme.Text(g, Fmt.Pct(cpu.Total), Theme.Big, Theme.LoadColor(cpu.Total), pad, Theme.K(20), Theme.K(70), Theme.K(28));

            float rightW = Theme.K(72);
            float x0 = pad + Theme.K(70), x1 = w - pad - rightW - 8;
            float[] loads = cpu.ThreadLoad;
            int n = loads.Length;
            const float gap = 1f;
            float bw = Math.Max(1.5f, (x1 - x0 - gap * (n - 1)) / n);
            float baseY = h - Theme.K(8), inner = h - Theme.K(18);
            for (int i = 0; i < n; i++)
            {
                float v = Fmt.Clamp(loads[i], 0, 100) / 100f;
                float bh = Math.Max(2, inner * v);
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.LoadColor(loads[i]))))
                    g.FillRectangle(b, x0 + i * (bw + gap), baseY - bh, bw, bh);
            }
            float rx = w - pad - rightW;
            float ly = Theme.K(13);
            Theme.Text(g, cpu.ClockMHz > 0 ? Fmt.MHz(cpu.ClockMHz) : ShortName(cpu.Name), Theme.Small, Theme.Fore, rx, Theme.K(8), rightW, 12, StringAlignment.Far);
            if (cpu.PackageTemp.HasValue)
                Theme.Text(g, Fmt.Temp(cpu.PackageTemp), Theme.SmallBold, Theme.TempColor(cpu.PackageTemp), rx, Theme.K(8) + ly, rightW, 12, StringAlignment.Far);
            else if (!Data.IsAdmin)
                Link(g, "admin for temps", w - pad, Theme.K(8) + ly, Theme.Orange, App.RestartElevated, StringAlignment.Far);
            ulong ramUsed = cpu.RamTotal > cpu.RamAvail ? cpu.RamTotal - cpu.RamAvail : 0;
            float ramPct = cpu.RamTotal > 0 ? 100f * ramUsed / cpu.RamTotal : 0;
            Theme.Text(g, "RAM " + Fmt.Pct(ramPct), Theme.Small, Theme.Dim, rx, Theme.K(8) + ly * 2, rightW, 12, StringAlignment.Far);
            return h;
        }

        private float DrawNotice(Graphics g, float w, float y, CpuSnap cpu)
        {
            if (Data == null) return y;
            bool anyTemp = cpu.PackageTemp.HasValue || (cpu.ThreadTemp?.Any(t => t.HasValue) ?? false);
            if (anyTemp) return y;

            if (!Data.SensorsReady)
                return Notice(g, w, y, "Loading temperature sensors…", null);
            if (!Data.IsAdmin)
                return Notice(g, w, y, "Run as admin for temperatures", App.RestartElevated);
            if (!Data.PawnIoInstalled)
                return Notice(g, w, y, "Install PawnIO for CPU temps", () => App.OpenUrl("https://pawnio.eu/"));
            return y;
        }

        protected override void AddMenuItems(ToolStripItemCollection items)
        {
            var rows = new ToolStripMenuItem("Thread display");
            string cur = RowSetting;
            rows.DropDownItems.Add(Check("Follow the design", cur == "auto" || cur == "threads", () => RowSetting = "auto"));
            rows.DropDownItems.Add(new ToolStripSeparator());
            foreach (var (mode, label) in new[]
            {
                (RowMode.List, "Every thread, one column"),
                (RowMode.TwoColumns, "Every thread, two columns"),
                (RowMode.Cores, "Physical cores"),
                (RowMode.Grid, "Heat grid"),
                (RowMode.Equalizer, "Equalizer bars"),
                (RowMode.Dial, "Dial"),
                (RowMode.Hidden, "Hidden"),
            })
            {
                RowMode m = mode;
                rows.DropDownItems.Add(Check(label, cur == m.ToString(), () => RowSetting = m.ToString()));
            }
            items.Add(rows);
            items.Add(Check("Show memory", ShowMemory, () => ShowMemory = !ShowMemory));
            items.Add(Check("Show history graph", ShowGraph, () => ShowGraph = !ShowGraph));
        }
    }
}
