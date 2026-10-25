using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DeskMeters.Sources;

namespace DeskMeters.Meters
{
    internal sealed class DrivesMeter : MeterWindow
    {
        private sealed class History
        {
            public readonly Ring Read = new Ring(120);
            public readonly Ring Write = new Ring(120);
        }

        private readonly Dictionary<string, History> _history = new Dictionary<string, History>(StringComparer.OrdinalIgnoreCase);

        public DrivesMeter(MeterApp app) : base(app) { }

        public override string Key => "drives";
        public override string Title => "Drives";

        private bool ShowGraphs
        {
            get => S.GetBool("drives.graphs", true);
            set { S.SetBool("drives.graphs", value); Render(); }
        }

        private bool ShowRates
        {
            get => S.GetBool("drives.rates", true);
            set { S.SetBool("drives.rates", value); Render(); }
        }

        public bool IncludeMapped
        {
            get => S.GetBool("drives.network", true);
            set { S.SetBool("drives.network", value); App.ApplySamplerOptions(); }
        }

        private bool HideEmpty
        {
            get => S.GetBool("drives.hideempty", false);
            set { S.SetBool("drives.hideempty", value); Render(); }
        }

        protected override void OnData(Snapshot data)
        {
            if (data.Drives == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DriveSnap d in data.Drives)
            {
                if (d.Key == null) continue;
                seen.Add(d.Key);
                if (!_history.TryGetValue(d.Key, out History h))
                    _history[d.Key] = h = new History();
                h.Read.Push((float)d.ReadBps);
                h.Write.Push((float)d.WriteBps);
            }
            foreach (string key in new List<string>(_history.Keys))
                if (!seen.Contains(key)) _history.Remove(key);
        }

        private DriveSnap[] Filtered(DriveSnap[] drives)
        {
            if (drives == null) return null;
            return HideEmpty ? drives.Where(d => d.Ready || d.Network).ToArray() : drives;
        }

        protected override float Draw(Graphics g, float w)
        {
            Skin skin = Theme.Skin;
            DriveSnap[] drives = Filtered(Data?.Drives);
            if (skin.Layout == MeterLayout.Strip)
                return DrawStrip(g, w, drives);

            long free = drives?.Where(d => d.Ready).Sum(d => d.Free) ?? 0;
            float busiest = drives?.Where(d => d.Busy.HasValue).Select(d => d.Busy.Value).DefaultIfEmpty(0).Max() ?? 0;
            string subtitle = drives == null ? "Waiting for data…" : drives.Length + (drives.Length == 1 ? " drive" : " drives") + " · " + Fmt.Bytes(free) + " free";

            float y = skin.BigHeader
                ? Header(g, w, Glyphs.Drive, "Drives", Fmt.Bytes(free), Theme.Title, drives == null ? subtitle : drives.Length + " drives", "free", busiest > 0 ? Fmt.Pct(busiest) + " busy" : null)
                : Header(g, w, Glyphs.Drive, "Drives", null, Theme.Fore, skin.PanelStyle == PanelStyle.Light || skin.Layout == MeterLayout.Rings ? subtitle : null);

            if (drives == null)
            {
                if (!skin.BigHeader && skin.PanelStyle != PanelStyle.Light) Theme.Text(g, subtitle, Theme.Body, Theme.Dim, Theme.Pad, y, w - Theme.Pad * 2);
                return y + Theme.Row + 10;
            }

            if (skin.Layout == MeterLayout.Equalizer && drives.Length > 0)
                y = DrawColumns(g, w, y, drives);

            for (int i = 0; i < drives.Length; i++)
            {
                if (i > 0 || skin.Layout == MeterLayout.Equalizer)
                {
                    y = Rule(g, w, y);
                }
                y = skin.Layout == MeterLayout.Rings ? DrawDriveRing(g, w, y, drives[i])
                  : skin.Layout == MeterLayout.Equalizer ? DrawDriveLine(g, w, y, drives[i])
                  : DrawDrive(g, w, y, drives[i]);
            }

            if (drives.Length > 0)
                y = Rule(g, w, y);

            if (skin.StatTiles)
            {
                Button(g, "+ Add network location", Theme.Pad, y, Theme.LinkColor, App.AddNetworkLocation, w - Theme.Pad * 2);
                y += ButtonHeight;
            }
            else
            {
                Link(g, "+ Add network location", w / 2, y, Theme.LinkColor, App.AddNetworkLocation, StringAlignment.Center);
                y += Theme.Row;
            }
            return y + Theme.K(6);
        }

        /// <summary>Hairline between drives. Returns the y below it.</summary>
        private static float Rule(Graphics g, float w, float y)
        {
            if (Theme.Skin.HeaderRule)
            {
                Theme.Divider(g, y + Theme.K(2), w);
                return y + Theme.K(8);
            }
            Theme.Divider(g, y + 1, w);
            return y + Theme.K(5);
        }

        private static string NameOf(DriveSnap d)
        {
            return d.Letter != null
                ? d.Letter + (string.IsNullOrEmpty(d.Label) ? "" : "  " + d.Label)
                : d.Label ?? d.Path;
        }

        private float DrawDrive(Graphics g, float w, float y, DriveSnap d)
        {
            Skin skin = Theme.Skin;
            float pad = Theme.Pad;
            float bw = w - pad * 2;
            string name = NameOf(d);
            bool compact = skin.Compact;
            float tagW = d.Network ? Theme.K(28) : 0;
            float nameMax = bw - Theme.K(40) - tagW;
            if (!string.IsNullOrEmpty(d.Path))
                Link(g, name, pad, y, Theme.Title, () => App.OpenUrl(d.Path), StringAlignment.Near, nameMax, compact);
            else
                Theme.Text(g, name, compact ? Theme.BodyBold : Theme.Body, Theme.Title, pad, y, nameMax);

            if (d.Network)
            {
                string shown = skin.Monospace && skin.BarStyle == BarStyle.Blocks ? "[" + name.ToLowerInvariant() + "]" : name;
                float nx = Math.Min(pad + Theme.Measure(g, shown, compact ? Theme.LinkBold : Theme.LinkFont) + (compact ? 5 : 7), pad + nameMax + 5);
                Theme.Text(g, "NET", Theme.Caption, Theme.Cyan, nx, y, tagW);
            }

            if (!d.Ready)
            {
                string state = d.Network ? (d.Checking ? "…" : "OFFLINE") : "EMPTY";
                Theme.Text(g, state, Theme.BodyBold, d.Network && !d.Checking ? Theme.Orange : Theme.Faint, pad, y, bw, Theme.Row, StringAlignment.Far);
                return y + Theme.Row + 1;
            }

            if (d.Busy.HasValue)
                Theme.Text(g, Fmt.Pct(d.Busy), Theme.BodyBold, Theme.LoadColor(d.Busy.Value), pad, y, bw, Theme.Row, StringAlignment.Far);
            y += Theme.Row + 1;

            long used = Math.Max(0, d.Total - d.Free);
            float pct = d.Total > 0 ? 100f * used / d.Total : 0;
            bool labelsInBar = skin.BarStyle == BarStyle.Lcd || skin.BarStyle == BarStyle.Thin || skin.BarStyle == BarStyle.Blocks;
            if (!labelsInBar)
            {
                Theme.Text(g, "Used " + Fmt.Bytes(used), Theme.Small, Theme.Fore, pad, y, w / 2 + 10);
                Theme.Text(g, "Free " + Fmt.Bytes(d.Free), Theme.Small, pct >= 92 ? Theme.Red : Theme.Fore, pad, y, bw, Theme.Row, StringAlignment.Far);
                y += Theme.Row;
                y += Theme.Bar(g, pad, y + 1, bw, pct, Theme.FullnessColor(pct), Fmt.Bytes(d.Total), Fmt.Pct(pct)) + Theme.K(4);
            }
            else
            {
                y += Theme.Bar(g, pad, y, bw, pct, Theme.FullnessColor(pct), Fmt.Bytes(used) + " used", Fmt.Bytes(d.Free) + " free") + 3;
            }

            if (d.HasRates && ShowRates)
            {
                Font rf = compact ? Theme.SmallBold : Theme.Small;
                Theme.Text(g, "R " + Fmt.BytesPerSec(d.ReadBps), rf, Theme.Green, pad, y, w / 2);
                Theme.Text(g, "W " + Fmt.BytesPerSec(d.WriteBps), rf, Theme.Orange, pad, y, bw, Theme.Row, StringAlignment.Far);
                y += Theme.Row;

                if (ShowGraphs && _history.TryGetValue(d.Key, out History h))
                {
                    float max = Math.Max(Math.Max(h.Read.Max(80), h.Write.Max(80)), 64 * 1024);
                    float gh = Theme.K(skin.GraphStyle == GraphStyle.HeatStrip ? 14 : 24);
                    Theme.Graph(g, new RectangleF(pad, y + 1, bw, gh), max, (h.Read, Theme.Green, true, 1.2f), (h.Write, Theme.Orange, true, 1.2f));
                    y += gh + Theme.K(3);
                }
            }

            return y;
        }

        /// <summary>Rings design: a ring with the letter inside, details to the right.</summary>
        private float DrawDriveRing(Graphics g, float w, float y, DriveSnap d)
        {
            float pad = Theme.Pad;
            float r = Theme.K(19);
            float cx = pad + r + 1, cy = y + r + 1;
            long used = Math.Max(0, d.Total - d.Free);
            float pct = d.Total > 0 ? 100f * used / d.Total : 0;
            Color color = !d.Ready ? Theme.Faint : d.Network ? Theme.Cyan : Theme.FullnessColor(pct);

            if (d.Ready)
                Theme.Ring(g, cx, cy, r, Theme.K(6), pct, color);
            else
                using (var pen = new Pen(Color.FromArgb(120, d.Network ? Theme.Orange : Theme.Faint), Theme.K(6)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                    g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);

            string letter = d.Letter != null ? d.Letter.TrimEnd(':') : d.Network ? "N" : "?";
            Theme.Text(g, letter, Theme.BodyBold, d.Ready ? Theme.Title : Theme.Faint, cx - r, cy - 7, r * 2, 14, StringAlignment.Center);

            float tx = cx + r + Theme.K(8);
            float tw = w - pad - tx;
            string name = d.Letter != null ? (string.IsNullOrEmpty(d.Label) ? (d.Network ? "Network share" : "Local disk") : d.Label) : d.Label ?? d.Path;
            if (!string.IsNullOrEmpty(d.Path))
                Link(g, name, tx, y, Theme.Title, () => App.OpenUrl(d.Path), StringAlignment.Near, tw - (d.Network ? 30 : 36));
            else
                Theme.Text(g, name, Theme.Body, Theme.Title, tx, y, tw - 30);
            if (d.Network)
                Theme.Text(g, "NET", Theme.Caption, Theme.Cyan, pad, y + 1, w - pad * 2, 12, StringAlignment.Far);
            else if (d.Busy.HasValue)
                Theme.Text(g, Fmt.Pct(d.Busy), Theme.BodyBold, Theme.LoadColor(d.Busy.Value), pad, y, w - pad * 2, Theme.Row, StringAlignment.Far);

            if (!d.Ready)
            {
                Theme.Text(g, d.Network ? (d.Checking ? "Checking…" : "Offline") : "No disc", Theme.Small, d.Network && !d.Checking ? Theme.Orange : Theme.Dim, tx, y + Theme.Row, tw, Theme.Row);
            }
            else
            {
                Theme.Text(g, Fmt.Bytes(d.Free) + " free of " + Fmt.Bytes(d.Total), Theme.Small, pct >= 92 ? Theme.Red : Theme.Dim, tx, y + Theme.Row, tw, Theme.Row);
                string third = d.HasRates && ShowRates ? "R " + Fmt.BytesPerSec(d.ReadBps) + "  W " + Fmt.BytesPerSec(d.WriteBps) : Fmt.Pct(pct) + " full";
                Theme.Text(g, third, Theme.Small, Theme.Dim, tx, y + Theme.Row * 2, tw, Theme.Row);
            }
            return y + Math.Max(r * 2 + 2, Theme.Row * 3) + 2;
        }

        /// <summary>Equalizer design: all drives as vertical columns in one box.</summary>
        private float DrawColumns(Graphics g, float w, float y, DriveSnap[] drives)
        {
            float pad = Theme.Pad;
            float boxH = Theme.K(100);
            var box = new RectangleF(pad, y, w - pad * 2, boxH);
            Theme.Fill(g, box, 5, Theme.GraphBack);
            int n = drives.Length;
            float colW = Math.Min(Theme.K(28), (box.Width - 12 - 6 * (n - 1)) / n);
            float totalW = colW * n + 6 * (n - 1);
            float x0 = box.X + (box.Width - totalW) / 2;
            float top = box.Y + Theme.K(16), bottom = box.Bottom - Theme.K(16);
            float inner = bottom - top;

            for (int i = 0; i < n; i++)
            {
                DriveSnap d = drives[i];
                float x = x0 + i * (colW + 6);
                long used = Math.Max(0, d.Total - d.Free);
                float pct = d.Ready && d.Total > 0 ? 100f * used / d.Total : 0;
                var track = new RectangleF(x, top, colW, inner);
                if (d.Ready)
                {
                    Theme.Fill(g, track, 3, Theme.Track);
                    float h = Math.Max(2, inner * pct / 100f);
                    Color c = d.Network ? Theme.Cyan : Theme.FullnessColor(pct);
                    using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(0, bottom - h), new PointF(0, bottom + 0.01f), Theme.Lighten(c, 0.25f, 255), Theme.Darken(c, 0.2f, 255)))
                        g.FillRectangle(b, x, bottom - h, colW, h);
                    using (var cut = new SolidBrush(Color.FromArgb(255, 12, 14, 18)))
                        for (float sy = bottom - 5; sy > bottom - h; sy -= 5)
                            g.FillRectangle(cut, x, sy, colW, 1);
                    Theme.Text(g, Fmt.Pct(pct), Theme.Caption, pct >= 92 ? Theme.Red : Theme.Fore, x - 6, top - 13, colW + 12, 11, StringAlignment.Center);
                }
                else
                {
                    using (var pen = new Pen(Color.FromArgb(70, d.Network ? Theme.Orange : Theme.Faint), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                        g.DrawRectangle(pen, x, top, colW, inner);
                    Theme.Text(g, d.Network ? "off" : "--", Theme.Caption, d.Network ? Theme.Orange : Theme.Faint, x - 6, top - 13, colW + 12, 11, StringAlignment.Center);
                }
                string letter = d.Letter != null ? d.Letter.TrimEnd(':') : "N";
                Theme.Text(g, letter, Theme.SmallBold, Theme.Title, x - 6, bottom + 2, colW + 12, 12, StringAlignment.Center);
            }
            return y + boxH + 4;
        }

        /// <summary>Equalizer design: one line of detail per drive under the columns.</summary>
        private float DrawDriveLine(Graphics g, float w, float y, DriveSnap d)
        {
            float pad = Theme.Pad;
            string name = NameOf(d);
            if (!string.IsNullOrEmpty(d.Path))
                Link(g, name, pad, y, Theme.Title, () => App.OpenUrl(d.Path), StringAlignment.Near, w - pad * 2 - 70);
            else
                Theme.Text(g, name, Theme.Body, Theme.Title, pad, y, w - pad * 2 - 70);
            string right = !d.Ready ? (d.Network ? (d.Checking ? "…" : "offline") : "no disc")
                         : d.HasRates && ShowRates && (d.ReadBps + d.WriteBps) > 1024 ? "R " + Fmt.BytesPerSec(d.ReadBps) + " W " + Fmt.BytesPerSec(d.WriteBps)
                         : Fmt.Bytes(d.Free) + " free";
            Color rc = !d.Ready ? (d.Network && !d.Checking ? Theme.Orange : Theme.Faint) : d.Total > 0 && (d.Total - d.Free) * 100 / d.Total >= 92 ? Theme.Red : Theme.Dim;
            Theme.Text(g, right, Theme.Small, rc, pad, y, w - pad * 2, Theme.Row, StringAlignment.Far);
            return y + Theme.Row;
        }

        private float DrawStrip(Graphics g, float w, DriveSnap[] drives)
        {
            float pad = Theme.Pad;
            float h = Theme.K(54);
            float ty = Theme.K(8);
            Glyphs.Drive(g, pad, Theme.K(10), Theme.K(14), Theme.Accent);
            int offline = drives?.Count(d => d.Network && !d.Ready && !d.Checking) ?? 0;
            Theme.Text(g, "Drives", Theme.Caption, Theme.Dim, pad + Theme.K(18), ty, 80, 12);
            if (offline > 0)
                Theme.Text(g, offline + " offline", Theme.Caption, Theme.Orange, pad + Theme.K(18), Theme.K(20), 80, 12);
            if (drives == null)
            {
                Theme.Text(g, "Waiting for data…", Theme.Body, Theme.Dim, pad + Theme.K(18), Theme.K(24), w - pad * 2 - 18, Theme.Row);
                return h;
            }

            DriveSnap[] ready = drives.Where(d => d.Ready).Take(4).ToArray();
            int n = Math.Max(1, ready.Length);
            float x = pad + Theme.K(52);
            float colW = (w - pad - x - 8 * (n - 1)) / n;
            foreach (DriveSnap d in ready)
            {
                long used = Math.Max(0, d.Total - d.Free);
                float pct = d.Total > 0 ? 100f * used / d.Total : 0;
                string letter = d.Letter ?? "NET";
                Link(g, letter, x, ty, Theme.Title, () => App.OpenUrl(d.Path), StringAlignment.Near, colW / 2, Theme.Compact);
                Theme.Text(g, Fmt.Bytes(d.Free) + " free", Theme.Small, pct >= 92 ? Theme.Red : Theme.Dim, x, ty, colW, 12, StringAlignment.Far);
                float by = Theme.K(24);
                Theme.Fill(g, new RectangleF(x, by, colW, 6), 3, Theme.Track);
                Theme.Fill(g, new RectangleF(x, by, Math.Max(3, colW * pct / 100f), 6), 3, d.Network ? Theme.Cyan : Theme.FullnessColor(pct));
                string third = d.HasRates ? (d.Busy.HasValue ? Fmt.Pct(d.Busy) + " · " : "") + "R " + Fmt.BytesPerSec(d.ReadBps) + " W " + Fmt.BytesPerSec(d.WriteBps) : d.Network ? "network" : "";
                Theme.Text(g, third, Theme.Caption, Theme.Faint, x, by + Theme.K(10), colW, 11);
                x += colW + 8;
            }
            if (ready.Length == 0)
                Theme.Text(g, "No drives ready", Theme.Body, Theme.Dim, pad + Theme.K(52), Theme.K(22), w - pad * 2 - Theme.K(52), Theme.Row);
            return h;
        }

        protected override void AddMenuItems(ToolStripItemCollection items)
        {
            items.Add(Check("Add network location…", false, App.AddNetworkLocation));

            List<NetLocation> custom = NetworkLocations.LoadCustom(S);
            if (custom.Count > 0)
            {
                var remove = new ToolStripMenuItem("Remove network location");
                foreach (NetLocation loc in custom)
                {
                    string path = loc.Path;
                    string label = string.IsNullOrEmpty(loc.Name) ? path : loc.Name + "  (" + path + ")";
                    remove.DropDownItems.Add(Check(label, false, () => App.RemoveNetworkLocation(path)));
                }
                items.Add(remove);
            }

            items.Add(Check("Show mapped network drives", IncludeMapped, () => IncludeMapped = !IncludeMapped));
            items.Add(Check("Hide empty drives (no disc)", HideEmpty, () => HideEmpty = !HideEmpty));
            items.Add(new ToolStripSeparator());
            items.Add(Check("Show read/write rates", ShowRates, () => ShowRates = !ShowRates));
            items.Add(Check("Show activity graphs", ShowGraphs, () => ShowGraphs = !ShowGraphs));
        }
    }
}
