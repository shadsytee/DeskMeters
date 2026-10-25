using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeskMeters.Meters
{
    internal sealed class GpuMeter : MeterWindow
    {
        private readonly Ring _load = new Ring(200);
        private readonly Ring _temp = new Ring(200);
        private readonly Ring _vram = new Ring(200);
        private string _historyFor;

        public GpuMeter(MeterApp app) : base(app) { }

        public override string Key => "gpu";
        public override string Title => "GPU";

        private bool ShowGraph
        {
            get => S.GetBool("gpu.graph", true);
            set { S.SetBool("gpu.graph", value); Render(); }
        }

        private bool ShowClocks
        {
            get => S.GetBool("gpu.clocks", true);
            set { S.SetBool("gpu.clocks", value); Render(); }
        }

        protected override void OnData(Snapshot data)
        {
            GpuSnap gpu = data.Gpu;
            if (gpu?.Name == null) return;
            if (gpu.Name != _historyFor)
            {
                _historyFor = gpu.Name;
                _load.Clear(); _temp.Clear(); _vram.Clear();
            }
            _load.Push(gpu.Load ?? 0);
            _temp.Push(gpu.Temp ?? 0);
            _vram.Push(gpu.MemTotalMB > 0 ? 100f * (gpu.MemUsedMB ?? 0) / gpu.MemTotalMB.Value : 0);
        }

        private static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            foreach (string prefix in new[] { "NVIDIA ", "AMD ", "Intel(R) ", "Intel " })
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(prefix.Length);
            return name;
        }

        protected override float Draw(Graphics g, float w)
        {
            Skin skin = Theme.Skin;
            GpuSnap gpu = Data?.Gpu;
            bool has = gpu?.Name != null;
            bool ready = Data != null && Data.SensorsReady;
            if (skin.Layout == MeterLayout.Strip)
                return DrawStrip(g, w, gpu, ready);

            string subtitle = !ready ? "Loading sensors…" : has ? ShortName(gpu.Name) : "No supported GPU found";
            string fan = has && (gpu.FanRpm.HasValue || gpu.FanPct.HasValue)
                ? (gpu.FanRpm.HasValue ? Fmt.Num(gpu.FanRpm.Value, "0") + " RPM" : "") + (gpu.FanPct.HasValue ? (gpu.FanRpm.HasValue ? " · " : "") + Fmt.Pct(gpu.FanPct) : "")
                : null;

            float y = Header(g, w, Glyphs.Card, "GPU", has ? Fmt.Pct(gpu.Load) : null, Theme.LoadColor(gpu?.Load ?? 0),
                             skin.BigHeader || skin.PanelStyle == PanelStyle.Light || skin.Monospace || skin.HeaderRule ? subtitle : null,
                             has ? Fmt.Temp(gpu.Temp) : null,
                             has && gpu.HotSpot.HasValue ? "hot " + Fmt.Temp(gpu.HotSpot) : null,
                             has && gpu.Power.HasValue ? Fmt.Watts(gpu.Power) : null);

            if (!ready || !has)
            {
                if (!skin.BigHeader && skin.PanelStyle != PanelStyle.Light && !skin.Monospace && !skin.HeaderRule)
                {
                    Theme.Text(g, subtitle, Theme.Body, Theme.Dim, Theme.Pad, y, w - Theme.Pad * 2);
                    y += Theme.Row;
                }
                if (ready && !has && !string.IsNullOrEmpty(Data.SensorError))
                    y = Notice(g, w, y + 2, "Sensor error – open log", App.OpenLog);
                return y + 8;
            }

            float pad = Theme.Pad;
            float bw = w - pad * 2;

            if (!skin.BigHeader && skin.PanelStyle != PanelStyle.Light && !skin.Monospace && !skin.HeaderRule)
            {
                Theme.Text(g, subtitle, Theme.Small, Theme.Dim, pad, y - 2, bw);
                y += Theme.Row + 1;
            }

            if (skin.Layout == MeterLayout.Rings)
            {
                y = DrawRings(g, w, y, gpu, fan);
            }
            else if (skin.StatTiles)
            {
                y = Tiles(g, w, y,
                          ("Temp", Fmt.Temp(gpu.Temp), Theme.TempColor(gpu.Temp), gpu.HotSpot.HasValue ? "hot spot " + Fmt.Temp(gpu.HotSpot) : null),
                          ("Clock", gpu.CoreClock.HasValue ? Fmt.MHz(gpu.CoreClock) : "--", Theme.Title, gpu.MemClock.HasValue ? "mem " + Fmt.Num(gpu.MemClock.Value / 1000.0, "0.0") + " GHz" : null));
                y = Tiles(g, w, y,
                          ("Fan", gpu.FanPct.HasValue ? Fmt.Pct(gpu.FanPct) : gpu.FanRpm.HasValue ? Fmt.Num(gpu.FanRpm.Value, "0") : "--", Theme.Title, gpu.FanRpm.HasValue ? Fmt.Num(gpu.FanRpm.Value, "0") + " RPM" : null),
                          ("Power", Fmt.Watts(gpu.Power), gpu.Power.HasValue ? Theme.Title : Theme.Faint, null));
            }
            else
            {
                if (!skin.BigHeader)
                {
                    Theme.Text(g, "Temp", Theme.Body, Theme.Dim, pad, y, w / 2);
                    string hot = gpu.HotSpot.HasValue ? "  hot " + Fmt.Temp(gpu.HotSpot) : "";
                    float hotW = Theme.Measure(g, hot, Theme.Small);
                    Theme.Text(g, hot, Theme.Small, Theme.TempColor(gpu.HotSpot), pad, y, bw, Theme.Row, StringAlignment.Far);
                    Theme.Text(g, Fmt.Temp(gpu.Temp), Theme.BodyBold, Theme.TempColor(gpu.Temp), pad, y, bw - hotW, Theme.Row, StringAlignment.Far);
                    y += Theme.Row;
                }
                if (ShowClocks)
                {
                    if (gpu.CoreClock.HasValue) y = Pair(g, w, y, "Core clock", Fmt.MHz(gpu.CoreClock), Theme.Fore);
                    if (gpu.MemClock.HasValue) y = Pair(g, w, y, "Memory clock", Fmt.MHz(gpu.MemClock), Theme.Fore);
                }
                if (fan != null) y = Pair(g, w, y, "Fan", fan, Theme.Fore);
                if (!skin.BigHeader && gpu.Power.HasValue) y = Pair(g, w, y, "Power", Fmt.Watts(gpu.Power), Theme.Fore);
            }

            // Bars
            float barGap = skin.HeaderRule ? Theme.Gap : skin.Compact ? 2 : 3;
            y += skin.HeaderRule ? Theme.Gap : Theme.K(4);
            float load = gpu.Load ?? 0;
            if (skin.Layout != MeterLayout.Rings)
                y += Theme.Bar(g, pad, y, bw, load, Theme.LoadColor(load), "Core", Fmt.Pct(gpu.Load)) + barGap;

            if (gpu.MemTotalMB.HasValue && skin.Layout != MeterLayout.Rings)
            {
                float used = gpu.MemUsedMB ?? 0;
                float pct = 100f * used / gpu.MemTotalMB.Value;
                string label = "VRAM " + Fmt.Bytes(used * 1048576.0) + (skin.Compact ? "/" : " / ") + Fmt.Bytes(gpu.MemTotalMB.Value * 1048576.0);
                y += Theme.Bar(g, pad, y, bw, pct, Theme.FullnessColor(pct), label, Fmt.Pct(pct)) + barGap;
            }

            if (gpu.MemCtrlLoad.HasValue)
                y += Theme.Bar(g, pad, y, bw, gpu.MemCtrlLoad.Value, Theme.Purple, "Memory ctrl", Fmt.Pct(gpu.MemCtrlLoad)) + barGap;

            if (gpu.VideoLoad.HasValue && gpu.VideoLoad > 0.5f)
                y += Theme.Bar(g, pad, y, bw, gpu.VideoLoad.Value, Theme.Cyan, "Video engine", Fmt.Pct(gpu.VideoLoad)) + barGap;

            if (ShowGraph)
            {
                y += skin.HeaderRule ? 0 : Theme.K(3);
                float gh = skin.GraphStyle == GraphStyle.HeatStrip ? Theme.K(34) : Theme.G(skin.HeaderRule ? 50 : 46);
                var graph = new RectangleF(pad, y, bw, gh);
                Theme.Graph(g, graph, 100, (_vram, Theme.Blue, false, 1f), (_temp, Theme.Orange, false, 1.2f), (_load, Theme.Green, true, 1.4f));
                if (skin.HeaderRule)
                {
                    // Legend under the graph: "● load  ● temp  ● vram"
                    y += gh + Theme.Gap;
                    float lx = pad;
                    float lh = skin.Compact ? 10 : 11;
                    foreach (var (name, color) in new[] { ("load", Theme.Green), ("temp", Theme.Orange), ("vram", Theme.Blue) })
                    {
                        if (name == "vram" && !gpu.MemTotalMB.HasValue) continue;
                        string item = "● " + name;
                        Theme.Text(g, item, Theme.Caption, color, lx, y, 60, lh);
                        lx += Theme.Measure(g, item, Theme.Caption) + (skin.Compact ? 7 : 8);
                    }
                    return y + lh + (skin.Compact ? 7 : 11);
                }
                if (skin.GraphStyle == GraphStyle.Area || skin.GraphStyle == GraphStyle.Line)
                {
                    float lx = graph.X + 3;
                    foreach (var (name, color) in new[] { ("load", Theme.Green), ("temp", Theme.Orange), ("vram", Theme.Blue) })
                    {
                        if (name == "vram" && !gpu.MemTotalMB.HasValue) continue;
                        Theme.Text(g, name, Theme.Small, Color.FromArgb(200, color), lx, graph.Y + 1, 40, 11);
                        lx += Theme.Measure(g, name, Theme.Small) + 6;
                    }
                }
                y += gh + Theme.K(5);
            }

            return y + 2;
        }

        private float DrawRings(Graphics g, float w, float y, GpuSnap gpu, string fan)
        {
            float pad = Theme.Pad;
            float big = Theme.K(44);
            float cx = w / 2, cy = y + big + 4;
            float load = gpu.Load ?? 0;
            Theme.Ring(g, cx, cy, big, Theme.K(8), load, Theme.LoadColor(load));
            if (gpu.Temp.HasValue)
                Theme.Ring(g, cx, cy, big - Theme.K(12), Theme.K(5), Fmt.Clamp(gpu.Temp.Value, 0, 100), Theme.Orange, Color.FromArgb(14, 255, 255, 255));
            float bh = Theme.K(24);
            Theme.Text(g, Fmt.Pct(gpu.Load), Theme.Big, Theme.Title, cx - 40, cy - bh * 0.67f, 80, bh, StringAlignment.Center);
            Theme.Text(g, Fmt.Temp(gpu.Temp), Theme.Small, Theme.TempColor(gpu.Temp), cx - 40, cy + bh * 0.29f, 80, 12, StringAlignment.Center);
            y = cy + big + Theme.K(8);
            Theme.Text(g, "load", Theme.Caption, Theme.Green, pad, y, (w - pad * 2) / 2, 11, StringAlignment.Far);
            Theme.Text(g, "temp", Theme.Caption, Theme.Orange, w / 2 + 6, y, (w - pad * 2) / 2, 11);
            y += Theme.K(14);

            float colW = (w - pad * 2) / 2f;
            float r = Theme.K(20);
            float vramPct = gpu.MemTotalMB > 0 ? 100f * (gpu.MemUsedMB ?? 0) / gpu.MemTotalMB.Value : 0;
            var items = new[]
            {
                (value: Fmt.Pct(vramPct), color: Theme.FullnessColor(vramPct), pct: vramPct, label: gpu.MemTotalMB.HasValue ? "VRAM " + Fmt.Bytes(gpu.MemTotalMB.Value * 1048576.0) : "VRAM"),
                (value: gpu.FanPct.HasValue ? Fmt.Pct(gpu.FanPct) : "--", color: Theme.Cyan, pct: gpu.FanPct ?? 0, label: gpu.FanRpm.HasValue ? "Fan " + Fmt.Num(gpu.FanRpm.Value, "0") + " rpm" : "Fan"),
            };
            for (int i = 0; i < 2; i++)
            {
                float rx = pad + colW * i + colW / 2;
                Theme.Ring(g, rx, y + r + 2, r, Theme.Compact ? 4.5f : 5.5f, items[i].pct, items[i].color);
                Theme.Text(g, items[i].value, Theme.SmallBold, Theme.Title, rx - r, y + 2 + r - 6, r * 2, 12, StringAlignment.Center);
                Theme.Text(g, items[i].label, Theme.Small, Theme.Dim, rx - colW / 2, y + r * 2 + 6, colW, 12, StringAlignment.Center);
            }
            y += r * 2 + Theme.K(22);

            if (gpu.HotSpot.HasValue) y = Pair(g, w, y, "Hot spot", Fmt.Temp(gpu.HotSpot), Theme.TempColor(gpu.HotSpot));
            if (ShowClocks && gpu.CoreClock.HasValue)
                y = Pair(g, w, y, "Core / mem", Fmt.Num(gpu.CoreClock.Value, "0") + (gpu.MemClock.HasValue ? " / " + Fmt.Num(gpu.MemClock.Value, "0") : "") + " MHz", Theme.Fore);
            if (gpu.Power.HasValue) y = Pair(g, w, y, "Power", Fmt.Watts(gpu.Power), Theme.Fore);
            return y;
        }

        private float DrawStrip(Graphics g, float w, GpuSnap gpu, bool ready)
        {
            float pad = Theme.Pad;
            float h = Theme.K(54);
            Glyphs.Card(g, pad, Theme.K(10), Theme.K(14), Theme.Accent);
            Theme.Text(g, "GPU", Theme.Caption, Theme.Dim, pad + Theme.K(18), Theme.K(8), 60, 12);
            if (!ready || gpu?.Name == null)
            {
                Theme.Text(g, ready ? "No supported GPU" : "Loading sensors…", Theme.Body, Theme.Dim, pad + Theme.K(18), Theme.K(24), w - pad * 2 - 18, Theme.Row);
                return h;
            }
            Theme.Text(g, Fmt.Pct(gpu.Load), Theme.Big, Theme.LoadColor(gpu.Load ?? 0), pad, Theme.K(20), Theme.K(70), Theme.K(28));

            float rightW = Theme.K(84);
            float gx = Theme.K(70);
            var graph = new RectangleF(pad + gx, Theme.K(8), w - pad * 2 - gx - rightW - 8, h - Theme.K(16));
            Theme.Graph(g, graph, 100, false, (_temp, Theme.Orange, false, 1.1f), (_load, Theme.Green, true, 1.3f));

            float rx = w - pad - rightW;
            float ty = Theme.K(8), ly = Theme.K(13);
            Theme.Text(g, Fmt.Temp(gpu.Temp), Theme.SmallBold, Theme.TempColor(gpu.Temp), rx, ty, rightW, 12, StringAlignment.Far);
            float vramPct = gpu.MemTotalMB > 0 ? 100f * (gpu.MemUsedMB ?? 0) / gpu.MemTotalMB.Value : 0;
            Theme.Text(g, gpu.MemTotalMB.HasValue ? "VRAM " + Fmt.Pct(vramPct) : ShortName(gpu.Name), Theme.Small, Theme.Fore, rx, ty + ly, rightW, 12, StringAlignment.Far);
            Theme.Text(g, gpu.Power.HasValue ? Fmt.Watts(gpu.Power) : gpu.CoreClock.HasValue ? Fmt.MHz(gpu.CoreClock) : "", Theme.Small, Theme.Dim, rx, ty + ly * 2, rightW, 12, StringAlignment.Far);
            return h;
        }

        protected override void AddMenuItems(ToolStripItemCollection items)
        {
            string[] names = Data?.Gpu?.AllNames;
            if (names != null && names.Length > 1)
            {
                var pick = new ToolStripMenuItem("Graphics card");
                int selected = Data.Gpu.SelectedIndex;
                for (int i = 0; i < names.Length; i++)
                {
                    string n = names[i];
                    pick.DropDownItems.Add(Check(n, i == selected, () => App.SetGpu(n)));
                }
                items.Add(pick);
            }
            items.Add(Check("Show clocks", ShowClocks, () => ShowClocks = !ShowClocks));
            items.Add(Check("Show history graph", ShowGraph, () => ShowGraph = !ShowGraph));
        }
    }
}
