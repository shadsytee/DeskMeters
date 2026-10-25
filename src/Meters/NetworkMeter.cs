using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DeskMeters.Meters
{
    internal sealed class NetworkMeter : MeterWindow
    {
        private readonly Ring _up = new Ring(200);
        private readonly Ring _down = new Ring(200);
        private string _adapterForHistory;
        private string _copiedFlash;
        private DateTime _copiedUntil;

        public NetworkMeter(MeterApp app) : base(app) { }

        public override string Key => "net";
        public override string Title => "Network";

        private bool ShowDetails
        {
            get => S.GetBool("net.details", true);
            set { S.SetBool("net.details", value); Render(); }
        }

        private bool ShowTotals
        {
            get => S.GetBool("net.totals", true);
            set { S.SetBool("net.totals", value); Render(); }
        }

        private bool ShowLinks
        {
            get => S.GetBool("net.links", true);
            set { S.SetBool("net.links", value); Render(); }
        }

        protected override void OnData(Snapshot data)
        {
            NetSnap net = data.Net;
            if (net == null) return;
            if (net.AdapterId != _adapterForHistory)
            {
                _adapterForHistory = net.AdapterId;
                _up.Clear();
                _down.Clear();
            }
            _up.Push((float)net.UpBps);
            _down.Push((float)net.DownBps);
        }

        private static string Rate(double bytesPerSec, bool primary)
        {
            bool bits = App_RatesInBits;
            return primary == bits ? Fmt.BitsPerSec(bytesPerSec) : Fmt.BytesPerSec(bytesPerSec);
        }

        private static bool App_RatesInBits => MeterApp.RatesInBits;

        protected override float Draw(Graphics g, float w)
        {
            Skin skin = Theme.Skin;
            NetSnap net = Data?.Net;
            if (skin.Layout == MeterLayout.Strip)
                return DrawStrip(g, w, net);

            string total = net != null ? Fmt.BytesPerSec(net.UpBps + net.DownBps) : null;
            string subtitle = net == null ? "Waiting for data…" : ShortAdapter(net.AdapterName);
            string link = net != null ? Fmt.LinkSpeed(net.LinkSpeed) : "";

            float y;
            if (skin.BigHeader)
            {
                y = Header(g, w, Glyphs.Globe, "Network", total, Theme.Title, subtitle,
                           net != null ? "▲ " + Rate(net.UpBps, true) : null,
                           net != null ? "▼ " + Rate(net.DownBps, true) : null,
                           link.Length > 0 ? link : null);
            }
            else
            {
                y = Header(g, w, Glyphs.Globe, "Network", null, Theme.Fore, null);
                if (total != null)
                {
                    if (skin.HeaderRule)
                        Theme.Text(g, total, Theme.Medium, Theme.Fore, Theme.Pad, HeaderTop, w - Theme.Pad * 2, 18, StringAlignment.Far);
                    else
                        Theme.Text(g, total, Theme.BodyBold, Theme.Fore, Theme.Pad, Theme.K(skin.PanelStyle == PanelStyle.Light ? 9 : 8), w - Theme.Pad * 2, Theme.K(14), StringAlignment.Far);
                }
            }

            if (net == null)
            {
                if (!skin.BigHeader) Theme.Text(g, subtitle, Theme.Body, Theme.Dim, Theme.Pad, y, w - Theme.Pad * 2);
                return y + Theme.Row + 10;
            }

            float pad = Theme.Pad;
            float bw = w - pad * 2;

            if (!skin.BigHeader)
            {
                float sy = skin.HeaderRule ? y : y - 2;
                float sh = skin.HeaderRule ? 12 : Theme.Row;
                float linkW = link.Length > 0 ? Theme.Measure(g, link, Theme.Small) + 6 : 0;
                Theme.Text(g, subtitle, Theme.Small, Theme.Dim, pad, sy, bw - linkW, sh);
                if (link.Length > 0)
                    Theme.Text(g, link, Theme.Small, Theme.Faint, pad, sy, bw, sh, StringAlignment.Far);
                y += skin.HeaderRule ? 12 + Theme.Gap : Theme.Row + 1;
            }

            // Rates: tiles, rings, or the classic two lines
            if (skin.StatTiles)
            {
                y = Tiles(g, w, y,
                          ("▲ Up", Rate(net.UpBps, true), Theme.Cyan, Rate(net.UpBps, false)),
                          ("▼ Down", Rate(net.DownBps, true), Theme.Green, Rate(net.DownBps, false)));
            }
            else if (skin.Layout == MeterLayout.Rings)
            {
                y = DrawRateRings(g, w, y, net);
            }
            else if (!skin.BigHeader)
            {
                if (ShowDetails) y = DrawDetails(g, w, y, net);
                if (skin.HeaderRule)
                {
                    float rh = skin.Compact ? 13 : 14;
                    Theme.Divider(g, y, w);
                    y += 1 + Theme.Gap;
                    y = RateRow(g, w, y, "▲", Theme.Cyan, net.UpBps) - Theme.Row + rh + Theme.Gap;
                    y = RateRow(g, w, y, "▼", Theme.Green, net.DownBps) - Theme.Row + rh + Theme.Gap;
                }
                else
                {
                    Theme.Divider(g, y, w);
                    y += Theme.K(4);
                    y = RateRow(g, w, y, "▲", Theme.Cyan, net.UpBps);
                    y = RateRow(g, w, y, "▼", Theme.Green, net.DownBps);
                    y += Theme.K(3);
                }
            }

            // Graph
            float max = Math.Max(_up.Max(80), _down.Max(80));
            float scale = NiceScale(max, out string scaleLabel);
            float gh = skin.GraphStyle == GraphStyle.HeatStrip ? Theme.K(34) : skin.Layout == MeterLayout.Equalizer ? Theme.K(56) : Theme.G(skin.HeaderRule ? 54 : 46);
            var graph = new RectangleF(pad, y, bw, gh);
            Theme.Graph(g, graph, scale, skin.Layout == MeterLayout.Equalizer, (_up, Theme.Cyan, true, 1.3f), (_down, Theme.Green, true, 1.3f));
            if (skin.GraphStyle == GraphStyle.Area || skin.GraphStyle == GraphStyle.Line)
                Theme.Text(g, scaleLabel, Theme.Small, Color.FromArgb(150, Theme.Fore), graph.X + 3, graph.Y + 1, graph.Width - 6, 11, StringAlignment.Far);
            if (skin.Layout == MeterLayout.Equalizer)
            {
                Theme.Text(g, "▲", Theme.Caption, Theme.Cyan, graph.X + 3, graph.Y + 2, 12, 10);
                Theme.Text(g, "▼", Theme.Caption, Theme.Green, graph.X + 3, graph.Bottom - 12, 12, 10);
            }
            y += gh + (skin.HeaderRule ? Theme.Gap : Theme.K(5));

            if ((skin.StatTiles || skin.Layout == MeterLayout.Rings || skin.BigHeader) && ShowDetails)
                y = DrawDetails(g, w, y, net);

            if (ShowTotals)
            {
                if (skin.StatTiles || skin.Layout == MeterLayout.Rings || skin.BigHeader)
                {
                    Theme.Divider(g, y + 1, w);
                    y += Theme.K(5);
                }
                y = TotalRow(g, w, y, "Session", net.SessionUp, net.SessionDown);
                y = TotalRow(g, w, y, "Total", net.TotalUp, net.TotalDown);
            }
            return y + Theme.K(skin.HeaderRule ? 7 : 5);
        }

        private float DrawDetails(Graphics g, float w, float y, NetSnap net)
        {
            Skin skin = Theme.Skin;
            float pad = Theme.Pad;
            y = IpRow(g, w, y, "Int. IP", net.IntIp);
            y = IpRow(g, w, y, "Ext. IP", net.ExtIp ?? "…");

            Theme.Text(g, skin.Compact ? "Blacklist" : "Blacklisted", Theme.Body, Theme.Dim, pad, y, w / 2);
            string bl = net.Blacklist ?? "…";
            Color blColor = net.BlacklistListed < 0 ? Theme.Dim : net.BlacklistListed == 0 ? Theme.Green : Theme.Red;
            Link(g, bl, w - pad, y, blColor, App.RecheckNetwork, StringAlignment.Far, 0, skin.Compact);
            y += Theme.Row;

            Theme.Text(g, "Firewall", Theme.Body, Theme.Dim, pad, y, w / 2);
            string fw = net.FirewallOn == null ? "--" : net.FirewallOn.Value ? "On" : "Off";
            Color fwColor = net.FirewallOn == null ? Theme.Dim : net.FirewallOn.Value ? Theme.Green : Theme.Red;
            string profile = string.IsNullOrEmpty(net.FirewallProfile) ? "" : " · " + net.FirewallProfile;
            float profW = Theme.Measure(g, profile, Theme.Body);
            Theme.Text(g, profile, Theme.Body, Theme.Fore, pad, y, w - pad * 2, Theme.Row, StringAlignment.Far);
            Theme.Text(g, fw, Theme.BodyBold, fwColor, pad, y, w - pad * 2 - profW, Theme.Row, StringAlignment.Far);
            y += Theme.Row + (skin.Compact ? 1 : 2);

            if (ShowLinks)
            {
                if (skin.StatTiles)
                {
                    float bx = pad;
                    float bg = Theme.K(5);
                    float bwidth = (w - pad * 2 - bg * 2) / 3f;
                    bx += Button(g, skin.Compact ? "Speed" : "Speed test", bx, y, Theme.Cyan, () => App.OpenUrl("https://www.speedtest.net/"), bwidth) + bg;
                    bx += Button(g, skin.Compact ? "IP info" : "IP lookup", bx, y, Theme.Cyan, () => App.OpenUrl(string.IsNullOrEmpty(net.ExtIp) ? "https://ipinfo.io/" : "https://ipinfo.io/" + net.ExtIp), bwidth) + bg;
                    Button(g, "Adapters", bx, y, Theme.Cyan, () => App.OpenUrl("ncpa.cpl"), bwidth);
                    y += ButtonHeight + bg;
                }
                else
                {
                    // Flow the links, wrapping when a wide font needs a second line.
                    var links = new List<(string text, Action click)>
                    {
                        ("Speed test", () => App.OpenUrl("https://www.speedtest.net/")),
                        (skin.Compact ? "IP info" : "IP lookup", () => App.OpenUrl(string.IsNullOrEmpty(net.ExtIp) ? "https://ipinfo.io/" : "https://ipinfo.io/" + net.ExtIp)),
                        ("Adapters", () => App.OpenUrl("ncpa.cpl")),
                    };
                    // Compact: spread the three links across the width, as in the gadget.
                    float spacing = skin.Compact ? 6 : 8;
                    float lx = pad;
                    float total = 0;
                    foreach (var (text, _) in links)
                        total += Theme.Measure(g, skin.Monospace && skin.BarStyle == BarStyle.Blocks ? "[" + text.ToLowerInvariant() + "]" : text, Theme.LinkFont) + 1;
                    if (skin.Compact && total + spacing * 2 <= w - pad * 2)
                        spacing = (w - pad * 2 - total) / 2;
                    foreach (var (text, click) in links)
                    {
                        string shown = skin.Monospace && skin.BarStyle == BarStyle.Blocks ? "[" + text.ToLowerInvariant() + "]" : text;
                        float tw = Theme.Measure(g, shown, Theme.LinkFont) + 1;
                        if (lx > pad && lx + tw > w - pad + 0.5f)
                        {
                            lx = pad;
                            y += Theme.Row;
                        }
                        lx += Link(g, text, lx, y, Theme.LinkColor, click) + spacing;
                    }
                    y += Theme.Row + (skin.HeaderRule ? Theme.Gap : Theme.K(4));
                }
            }
            return y;
        }

        private float DrawRateRings(Graphics g, float w, float y, NetSnap net)
        {
            float pad = Theme.Pad;
            float colW = (w - pad * 2) / 2f;
            float peak = Math.Max(1024, Math.Max(_up.Max(60), _down.Max(60)));
            float r = Theme.K(24);
            var items = new[]
            {
                (arrow: "▲", color: Theme.Cyan, rate: net.UpBps),
                (arrow: "▼", color: Theme.Green, rate: net.DownBps),
            };
            for (int i = 0; i < 2; i++)
            {
                float cx = pad + colW * i + colW / 2;
                Theme.Ring(g, cx, y + r + 2, r, Theme.K(6), (float)(100 * items[i].rate / peak), items[i].color);
                Theme.Text(g, items[i].arrow, Theme.BodyBold, items[i].color, cx - r, y + 2 + r - 7, r * 2, 14, StringAlignment.Center);
                Theme.Text(g, Rate(items[i].rate, true), Theme.SmallBold, Theme.Title, cx - colW / 2, y + r * 2 + 6, colW, 12, StringAlignment.Center);
                Theme.Text(g, Rate(items[i].rate, false), Theme.Small, Theme.Dim, cx - colW / 2, y + r * 2 + 6 + Theme.K(12), colW, 12, StringAlignment.Center);
            }
            y += r * 2 + 6 + Theme.K(12) + 14;
            Theme.Text(g, "vs. the last minute's peak", Theme.Caption, Theme.Faint, pad, y, w - pad * 2, 11, StringAlignment.Center);
            return y + Theme.K(14);
        }

        private float DrawStrip(Graphics g, float w, NetSnap net)
        {
            float pad = Theme.Pad;
            float h = Theme.K(54);
            float ty = Theme.K(8), ly = Theme.K(13);
            Glyphs.Globe(g, pad, Theme.K(10), Theme.K(14), Theme.Accent);
            Theme.Text(g, "Network", Theme.Caption, Theme.Dim, pad + Theme.K(18), ty, 80, 12);
            if (net == null)
            {
                Theme.Text(g, "Waiting for data…", Theme.Body, Theme.Dim, pad + Theme.K(18), Theme.K(24), w - pad * 2 - 18, Theme.Row);
                return h;
            }
            float leftW = Theme.K(100);
            Theme.Text(g, "▲ " + Rate(net.UpBps, true), Theme.SmallBold, Theme.Cyan, pad, ty + ly + 1, leftW, 12);
            Theme.Text(g, "▼ " + Rate(net.DownBps, true), Theme.SmallBold, Theme.Green, pad, ty + ly * 2 + 1, leftW, 12);

            float max = Math.Max(_up.Max(80), _down.Max(80));
            float scale = NiceScale(max, out _);
            float rightW = Theme.K(92);
            float gx = Theme.K(104);
            var graph = new RectangleF(pad + gx, ty, w - pad * 2 - gx - rightW - 8, h - Theme.K(16));
            Theme.Graph(g, graph, scale, false, (_up, Theme.Cyan, true, 1.2f), (_down, Theme.Green, true, 1.2f));

            float rx = w - pad - rightW;
            Link(g, net.ExtIp ?? "…", w - pad, ty, Theme.Fore, () => Copy(net.ExtIp), StringAlignment.Far);
            Theme.Text(g, net.IntIp ?? "", Theme.Small, Theme.Dim, rx, ty + ly, rightW, 12, StringAlignment.Far);
            string fw = net.FirewallOn == null ? "" : net.FirewallOn.Value ? "FW on" : "FW off";
            string bl = net.BlacklistListed < 0 ? "" : "RBL " + net.Blacklist;
            Theme.Text(g, (fw + "  " + bl).Trim(), Theme.Small, net.FirewallOn == false || net.BlacklistListed > 0 ? Theme.Red : Theme.Green, rx, ty + ly * 2, rightW, 12, StringAlignment.Far);
            return h;
        }

        private static string ShortAdapter(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            foreach (string tail in new[] { " Network Connection", " Network Adapter", " Family Controller", " Controller", " Adapter" })
                if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - tail.Length);
            return name;
        }

        private float IpRow(Graphics g, float w, float y, string label, string ip)
        {
            Theme.Text(g, label, Theme.Body, Theme.Dim, Theme.Pad, y, w / 2);
            if (string.IsNullOrEmpty(ip))
            {
                Theme.Text(g, "--", Theme.Body, Theme.Dim, Theme.Pad, y, w - Theme.Pad * 2, Theme.Row, StringAlignment.Far);
            }
            else
            {
                bool flash = _copiedFlash == ip && DateTime.Now < _copiedUntil;
                Link(g, flash ? "Copied" : ip, w - Theme.Pad, y, flash ? Theme.Green : Theme.Fore, () => Copy(ip), StringAlignment.Far, 0, Theme.Compact);
            }
            return y + Theme.Row;
        }

        private void Copy(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == "…") return;
            try
            {
                Clipboard.SetText(ip);
                _copiedFlash = ip;
                _copiedUntil = DateTime.Now.AddSeconds(1.5);
                Render();
            }
            catch (Exception ex)
            {
                Log.Error("Copy IP", ex);
            }
        }

        private static float RateRow(Graphics g, float w, float y, string arrow, Color c, double bytesPerSec)
        {
            Theme.Text(g, arrow, Theme.Small, c, Theme.Pad, y, 12);
            Theme.Text(g, Rate(bytesPerSec, true), Theme.BodyBold, c, Theme.Pad + 12, y, w / 2);
            Theme.Text(g, Rate(bytesPerSec, false), Theme.Body, Theme.Fore, Theme.Pad, y, w - Theme.Pad * 2, Theme.Row, StringAlignment.Far);
            return y + Theme.Row;
        }

        private static float TotalRow(Graphics g, float w, float y, string label, double up, double down)
        {
            float lw = Theme.K(44);
            Font f = Theme.Compact ? Theme.SmallBold : Theme.Small;
            float rh = Theme.Compact ? 12 : Theme.Row;
            Theme.Text(g, label, Theme.Small, Theme.Dim, Theme.Pad, y, lw, rh);
            float colW = (w - Theme.Pad * 2 - lw) / 2f;
            Theme.Text(g, "▲" + Fmt.Bytes(up), f, Theme.Cyan, Theme.Pad + lw, y, colW, rh, StringAlignment.Far);
            Theme.Text(g, "▼" + Fmt.Bytes(down), f, Theme.Green, Theme.Pad + lw + colW, y, colW, rh, StringAlignment.Far);
            return y + rh;
        }

        /// <summary>
        /// Round the graph ceiling up to 1/2/5 × 10^n in the nearest binary unit
        /// (5 MB/s, 200 KB/s…) so the scale is readable and doesn't jitter.
        /// </summary>
        private static float NiceScale(float max, out string label)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            if (max < 1024) max = 1024;
            int u = 0;
            double unit = 1;
            while (max / unit >= 500 && u < units.Length - 1) { unit *= 1024; u++; }

            double v = max / unit;
            double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double nice = 10 * p;
            foreach (double m in new[] { 1, 2, 5, 10 })
                if (m * p >= v) { nice = m * p; break; }

            label = Fmt.Num(nice, "0.##") + units[u] + "/s";
            return (float)(nice * unit);
        }

        protected override void AddMenuItems(ToolStripItemCollection items)
        {
            var adapter = new ToolStripMenuItem("Adapter");
            List<KeyValuePair<string, string>> list = Data?.Net?.Adapters;
            string pinned = S.Get("net.adapter");
            adapter.DropDownItems.Add(Check("Automatic", string.IsNullOrEmpty(pinned), () => App.SetAdapter(null)));
            if (list != null && list.Count > 0)
            {
                adapter.DropDownItems.Add(new ToolStripSeparator());
                foreach (var kv in list)
                {
                    string id = kv.Key;
                    adapter.DropDownItems.Add(Check(kv.Value, pinned == id, () => App.SetAdapter(id)));
                }
            }
            items.Add(adapter);
            items.Add(Check("Show IP, blacklist and firewall", ShowDetails, () => ShowDetails = !ShowDetails));
            items.Add(Check("Show links (speed test, IP lookup, adapters)", ShowLinks, () => ShowLinks = !ShowLinks));
            items.Add(Check("Show session and total data", ShowTotals, () => ShowTotals = !ShowTotals));
            items.Add(Check("Recheck IP and blacklists now", false, App.RecheckNetwork));
            items.Add(Check("Reset totals for this adapter", false, App.ResetNetworkTotals));
        }
    }
}
