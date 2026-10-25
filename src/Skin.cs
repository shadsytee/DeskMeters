using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DeskMeters
{
    internal enum PanelStyle { Glass, Light, Flat, Clipped, Bezel, None }
    internal enum BarStyle { Gradient, Flat, Blocks, Segmented, Lcd, Thin }
    internal enum GraphStyle { Area, Line, HeatStrip, Columns, LcdColumns }
    internal enum MeterLayout { Classic, Rings, Heatmap, Equalizer, Strip }
    internal enum RowMode { List, TwoColumns, Cores, Grid, Equalizer, Dial, Hidden }

    /// <summary>
    /// Everything that makes one design look the way it does. The meters read colours,
    /// fonts and styles from the current skin through <see cref="Theme"/>.
    /// </summary>
    internal sealed class Skin
    {
        public string Id;
        public string Name;
        public string Blurb;

        public MeterLayout Layout = MeterLayout.Classic;
        public RowMode DefaultRows = RowMode.List;

        public PanelStyle PanelStyle = PanelStyle.Glass;
        public BarStyle BarStyle = BarStyle.Gradient;
        public GraphStyle GraphStyle = GraphStyle.Area;

        public float Width = 168f;
        public float Pad = 9f;
        public float Radius = 8f;
        public float Row = 13f;
        public bool Shadow;            // soft drop shadow outside the panel
        public bool TextShadow;        // dark halo behind text (for use without a panel)
        public bool Glow;              // coloured glow behind bright text
        public bool Uppercase;         // labels in capitals
        public bool Sheen = false;     // glossy band across the top of the panel
        public bool HeaderRule;        // thin line under the header, mock-up spacing (Classic Refined)
        public bool GraphThreadLines = true;  // faint per-thread lines behind the CPU total
        public bool StatTiles;         // headline numbers in small tiles (Light)
        public bool RowBars;           // thread rows show a bar of the current load rather than a history spark

        /// <summary>Designs whose header is one big number rather than icon + title.</summary>
        public bool BigHeader => PanelStyle == PanelStyle.Clipped || PanelStyle == PanelStyle.Bezel || PanelStyle == PanelStyle.None;

        // ---- density ----------------------------------------------------------------------
        // The original ("roomy") values are the defaults below; Skins.Get(id, compact: true)
        // returns a copy with the tighter, gadget-sized values (Classic Compact mock-up).

        public bool Compact;
        public float Gap = 6f;          // space either side of a section rule (HeaderRule designs)
        public float ThreadRow = 12f;   // height of one CPU thread row
        public float Shrink = 1f;       // factor for fixed sizes (rings, boxes, strip layout)
        public float GraphScale = 1f;   // factor for history graph heights
        public float BarH = 12f;        // Gradient bars
        public float SlimBarH = 11f;    // Flat and Segmented bars
        public float IconPx = 16f;      // header icon (HeaderRule designs)
        public float TitlePx = 13f, ValuePx = 15f, MediumPx = 12f, BigPx = 26f, HugePx = 36f;
        public float BodyPx = 10.5f, SmallPx = 9.5f, CaptionPx = 8.5f, LinkPx = 10f, TilePx = 11.5f;

        /// <summary>Text always sits on an opaque panel, so ClearType can be used.</summary>
        public bool SharpText;

        public string FontFamily = "Segoe UI";
        public string BoldFamily = "Segoe UI Semibold";
        public float FontScale = 1f;   // VT323 needs bigger sizes for the same visual height
        public bool Monospace;

        public Color PanelTop, PanelBottom, Border, Highlight, Rule, Card;
        public Color Title, Fore, Dim, Faint;
        public Color Green, Yellow, Orange, Red, Cyan, Blue, Purple;
        public Color Track, GraphBack, Grid;
        public Color Accent;           // header icon / link colour
        public Color Link;

        /// <summary>Series colour for thread i of n; the default spreads hues.</summary>
        public Func<int, int, Color> Series;

        public Skin Clone() => (Skin)MemberwiseClone();
    }

    internal static class Skins
    {
        public const string DefaultId = "classic";

        private static readonly List<Skin> List = Build();

        public static IReadOnlyList<Skin> All => List;

        public static Skin Get(string id) => List.FirstOrDefault(s => s.Id == id) ?? List[0];

        private static readonly Dictionary<string, Skin> CompactCache = new Dictionary<string, Skin>();

        /// <summary>The design at the chosen density.</summary>
        public static Skin Get(string id, bool compact)
        {
            Skin s = Get(id);
            if (!compact) return s;
            if (!CompactCache.TryGetValue(s.Id, out Skin c))
                CompactCache[s.Id] = c = MakeCompact(s);
            return c;
        }

        /// <summary>
        /// Gadget-sized version of a design: about 150 px wide, 11 px thread rows, slightly
        /// smaller but brighter and bolder text, and an opaque panel so text gets ClearType.
        /// </summary>
        private static Skin MakeCompact(Skin b)
        {
            Skin s = b.Clone();
            s.Compact = true;

            s.Width = b.Layout == MeterLayout.Strip ? 290 : b.PanelStyle == PanelStyle.Bezel ? 158 : 150;
            s.Pad = b.PanelStyle == PanelStyle.Bezel ? 11 : b.PanelStyle == PanelStyle.None ? 5 : Math.Max(6, b.Pad - 3);
            s.Radius = (float)Math.Round(b.Radius * 0.8f);
            s.Row = b.PanelStyle == PanelStyle.Bezel ? 13 : b.Row >= 15 ? 13 : 12;
            s.Gap = 4;
            s.ThreadRow = 11;
            s.Shrink = 0.85f;
            s.GraphScale = 0.72f;
            s.BarH = 11;
            s.SlimBarH = 10;
            s.IconPx = 14;
            s.TitlePx = 12; s.ValuePx = 14; s.MediumPx = 11; s.BigPx = 22; s.HugePx = 30;
            s.BodyPx = 10; s.SmallPx = 9; s.CaptionPx = 8; s.LinkPx = 9.5f; s.TilePx = 10f;

            // Brighter text: body text moves toward the title colour, dim and faint text
            // toward the next level up. (For Light this darkens, which is the same idea.)
            s.Fore = Mix(b.Fore, b.Title, 0.55f);
            s.Dim = Mix(b.Dim, b.Fore, 0.3f);
            s.Faint = Mix(b.Faint, b.Dim, 0.4f);

            if (b.PanelStyle != PanelStyle.None)
            {
                s.PanelTop = Color.FromArgb(255, b.PanelTop);
                s.PanelBottom = Color.FromArgb(255, b.PanelBottom);
                s.SharpText = true;
            }
            return s;
        }

        private static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb(a.A,
                           (int)Math.Round(a.R + (b.R - a.R) * t),
                           (int)Math.Round(a.G + (b.G - a.G) * t),
                           (int)Math.Round(a.B + (b.B - a.B) * t));

        private static Color Rgb(int r, int g, int b) => Color.FromArgb(r, g, b);
        private static Color Argb(int a, int r, int g, int b) => Color.FromArgb(a, r, g, b);

        private static Skin Base()
        {
            // Palette shared by the dark designs: the one from the original meters.
            var s = new Skin
            {
                PanelTop = Argb(246, 40, 46, 56),
                PanelBottom = Argb(246, 20, 23, 29),
                Border = Argb(180, 96, 108, 126),
                Highlight = Argb(34, 255, 255, 255),
                Rule = Argb(26, 255, 255, 255),
                Card = Argb(14, 255, 255, 255),
                Title = Rgb(238, 241, 245),
                Fore = Rgb(214, 221, 230),
                Dim = Rgb(138, 150, 166),
                Faint = Rgb(96, 106, 120),
                Green = Rgb(120, 214, 140),
                Yellow = Rgb(246, 208, 84),
                Orange = Rgb(245, 156, 68),
                Red = Rgb(242, 88, 92),
                Cyan = Rgb(92, 200, 255),
                Blue = Rgb(110, 150, 255),
                Purple = Rgb(178, 141, 255),
                Track = Argb(22, 255, 255, 255),
                GraphBack = Argb(105, 0, 0, 0),
                Grid = Argb(16, 255, 255, 255),
                Accent = Rgb(92, 200, 255),
                Link = Rgb(92, 200, 255),
            };
            s.Series = (i, n) => Theme.FromHsv((i * 360.0 / Math.Max(1, n) + 200) % 360, 0.55, 1.0);
            return s;
        }

        private static List<Skin> Build()
        {
            var list = new List<Skin>();

            // 1 · Classic Refined (default)
            Skin classic = Base();
            classic.Id = "classic";
            classic.Name = "Classic Refined";
            classic.Blurb = "The original dark glass look, tidied up: aligned columns, steadier numbers, a soft shadow.";
            // Sizes and colours taken straight from the Classic Refined mock-up.
            classic.Width = 196;
            classic.Pad = 11;
            classic.Radius = 10;
            classic.Shadow = true;
            classic.HeaderRule = true;
            classic.Row = 15;
            classic.GraphThreadLines = false;
            classic.PanelTop = Argb(247, 40, 46, 56);
            classic.PanelBottom = Argb(247, 20, 23, 29);
            classic.Border = Argb(179, 96, 108, 126);
            classic.Highlight = Argb(20, 255, 255, 255);
            classic.Rule = Argb(23, 255, 255, 255);
            classic.Track = Argb(20, 255, 255, 255);
            classic.GraphBack = Argb(102, 0, 0, 0);
            classic.Grid = Argb(15, 255, 255, 255);
            list.Add(classic);

            // 2 · Light
            Skin light = Base();
            light.Id = "light";
            light.Name = "Light";
            light.Blurb = "White panels with darker accents. Thread list in two columns, stats in small tiles.";
            light.PanelStyle = PanelStyle.Light;
            light.BarStyle = BarStyle.Flat;
            light.DefaultRows = RowMode.TwoColumns;
            light.StatTiles = true;
            light.RowBars = true;
            light.Width = 176;
            light.Pad = 11;
            light.Radius = 11;
            light.Shadow = true;
            light.PanelTop = Argb(248, 250, 251, 253);
            light.PanelBottom = Argb(248, 243, 245, 249);
            light.Border = Argb(120, 180, 190, 205);
            light.Highlight = Argb(220, 255, 255, 255);
            light.Rule = Argb(255, 228, 232, 238);
            light.Card = Argb(255, 240, 243, 247);
            light.Title = Rgb(32, 38, 48);
            light.Fore = Rgb(42, 49, 59);
            light.Dim = Rgb(107, 118, 134);
            light.Faint = Rgb(150, 160, 175);
            light.Green = Rgb(46, 158, 87);
            light.Yellow = Rgb(201, 160, 0);
            light.Orange = Rgb(217, 118, 29);
            light.Red = Rgb(214, 64, 69);
            light.Cyan = Rgb(11, 143, 216);
            light.Blue = Rgb(59, 111, 240);
            light.Purple = Rgb(123, 85, 224);
            light.Track = Argb(255, 228, 232, 238);
            light.GraphBack = Argb(255, 240, 243, 247);
            light.Grid = Argb(16, 20, 30, 45);
            light.Accent = Rgb(11, 143, 216);
            light.Link = Rgb(11, 143, 216);
            light.Series = (i, n) => Theme.FromHsv((i * 360.0 / Math.Max(1, n) + 200) % 360, 0.7, 0.72);
            list.Add(light);

            // 3 · Terminal
            Skin term = Base();
            term.Id = "terminal";
            term.Name = "Terminal";
            term.Blurb = "Green on black in a monospace font, bars drawn from block characters.";
            term.PanelStyle = PanelStyle.Flat;
            term.BarStyle = BarStyle.Blocks;
            term.GraphStyle = GraphStyle.Line;
            term.FontFamily = "Consolas";
            term.BoldFamily = "Consolas";
            term.Monospace = true;
            term.RowBars = true;
            term.Radius = 0;
            term.Width = 176;
            term.Pad = 10;
            term.Row = 13;
            term.PanelTop = Argb(240, 6, 10, 8);
            term.PanelBottom = Argb(240, 6, 10, 8);
            term.Border = Rgb(42, 90, 56);
            term.Highlight = Argb(0, 0, 0, 0);
            term.Rule = Rgb(42, 90, 56);
            term.Card = Argb(20, 124, 242, 154);
            term.Title = Rgb(124, 242, 154);
            term.Fore = Rgb(124, 242, 154);
            term.Dim = Rgb(61, 122, 78);
            term.Faint = Rgb(42, 90, 56);
            term.Green = Rgb(124, 242, 154);
            term.Yellow = Rgb(255, 215, 94);
            term.Orange = Rgb(255, 180, 84);
            term.Red = Rgb(255, 107, 107);
            term.Cyan = Rgb(92, 224, 255);
            term.Blue = Rgb(124, 242, 154);
            term.Purple = Rgb(124, 242, 154);
            term.Track = Argb(30, 124, 242, 154);
            term.GraphBack = Argb(0, 0, 0, 0);
            term.Grid = Argb(18, 124, 242, 154);
            term.Accent = Rgb(124, 242, 154);
            term.Link = Rgb(124, 242, 154);
            term.Series = (i, n) => Rgb(124, 242, 154);
            list.Add(term);

            // 4 · Sci-fi HUD
            Skin hud = Base();
            hud.Id = "hud";
            hud.Name = "Sci-fi HUD";
            hud.Blurb = "Cyan outlines with clipped corners, big glowing numbers and segmented bars.";
            hud.PanelStyle = PanelStyle.Clipped;
            hud.BarStyle = BarStyle.Segmented;
            hud.GraphStyle = GraphStyle.Area;
            hud.DefaultRows = RowMode.TwoColumns;
            hud.FontFamily = "Rajdhani Medium";
            hud.BoldFamily = "Rajdhani SemiBold";
            hud.FontScale = 1.12f;
            hud.Uppercase = true;
            hud.RowBars = true;
            hud.Glow = true;
            hud.Width = 178;
            hud.Pad = 11;
            hud.Radius = 0;
            hud.PanelTop = Argb(242, 10, 24, 36);
            hud.PanelBottom = Argb(242, 5, 12, 20);
            hud.Border = Argb(160, 92, 200, 255);
            hud.Highlight = Argb(0, 0, 0, 0);
            hud.Rule = Argb(70, 92, 200, 255);
            hud.Card = Argb(18, 92, 200, 255);
            hud.Title = Rgb(234, 248, 255);
            hud.Fore = Rgb(191, 234, 255);
            hud.Dim = Rgb(75, 120, 145);
            hud.Faint = Rgb(55, 90, 110);
            hud.Track = Argb(30, 92, 200, 255);
            hud.GraphBack = Argb(50, 0, 0, 0);
            hud.Grid = Argb(16, 92, 200, 255);
            hud.Blue = Rgb(110, 150, 255);
            hud.Accent = Rgb(92, 200, 255);
            hud.Link = Rgb(92, 200, 255);
            hud.Series = (i, n) => Rgb(92, 200, 255);
            list.Add(hud);

            // 5 · Ring Gauges
            Skin rings = Base();
            rings.Id = "rings";
            rings.Name = "Ring Gauges";
            rings.Blurb = "CPU as a dial with every thread ticked around it; rings for temperature, memory, fan and each drive.";
            rings.Layout = MeterLayout.Rings;
            rings.DefaultRows = RowMode.Dial;
            rings.Width = 176;
            rings.Pad = 10;
            rings.Radius = 11;
            rings.Shadow = true;
            list.Add(rings);

            // 6 · Heatmap
            Skin heat = Base();
            heat.Id = "heatmap";
            heat.Name = "Heatmap";
            heat.Blurb = "Threads as a grid of tiles coloured by load; history as heat strips; drives as cell bars.";
            heat.Layout = MeterLayout.Heatmap;
            heat.DefaultRows = RowMode.Grid;
            heat.GraphStyle = GraphStyle.HeatStrip;
            heat.BarStyle = BarStyle.Segmented;
            heat.Width = 176;
            heat.Pad = 10;
            heat.Radius = 9;
            heat.Shadow = true;
            list.Add(heat);

            // 7 · Equalizer
            Skin eq = Base();
            eq.Id = "equalizer";
            eq.Name = "Equalizer";
            eq.Blurb = "Threads as stereo-style bars with peak caps; upload and download mirrored around a centre line.";
            eq.Layout = MeterLayout.Equalizer;
            eq.DefaultRows = RowMode.Equalizer;
            eq.GraphStyle = GraphStyle.Columns;
            eq.BarStyle = BarStyle.Segmented;
            eq.Width = 176;
            eq.Pad = 10;
            eq.Radius = 9;
            eq.Shadow = true;
            eq.PanelTop = Argb(246, 34, 38, 46);
            eq.PanelBottom = Argb(246, 14, 16, 20);
            eq.GraphBack = Argb(115, 0, 0, 0);
            list.Add(eq);

            // 8 · Retro LCD
            Skin lcd = Base();
            lcd.Id = "lcd";
            lcd.Name = "Retro LCD";
            lcd.Blurb = "Amber pixel display in a dark bezel, with lit and unlit segments.";
            lcd.PanelStyle = PanelStyle.Bezel;
            lcd.BarStyle = BarStyle.Lcd;
            lcd.GraphStyle = GraphStyle.LcdColumns;
            lcd.DefaultRows = RowMode.Equalizer;
            lcd.FontFamily = "VT323";
            lcd.BoldFamily = "VT323";
            lcd.FontScale = 1.5f;
            lcd.Monospace = true;
            lcd.Uppercase = true;
            lcd.Glow = true;
            lcd.Width = 176;
            lcd.Pad = 14;
            lcd.Radius = 7;
            lcd.Row = 14;
            lcd.PanelTop = Rgb(52, 54, 59);
            lcd.PanelBottom = Rgb(26, 27, 30);
            lcd.Border = Rgb(12, 12, 13);
            lcd.Highlight = Argb(30, 255, 255, 255);
            lcd.Rule = Rgb(122, 83, 0);
            lcd.Card = Argb(0, 0, 0, 0);
            lcd.Title = Rgb(255, 176, 0);
            lcd.Fore = Rgb(255, 176, 0);
            lcd.Dim = Rgb(170, 115, 0);
            lcd.Faint = Rgb(122, 83, 0);
            lcd.Green = Rgb(255, 176, 0);
            lcd.Yellow = Rgb(255, 176, 0);
            lcd.Orange = Rgb(255, 176, 0);
            lcd.Red = Rgb(255, 176, 0);
            lcd.Cyan = Rgb(255, 176, 0);
            lcd.Blue = Rgb(255, 176, 0);
            lcd.Purple = Rgb(255, 176, 0);
            lcd.Track = Argb(28, 255, 176, 0);
            lcd.GraphBack = Argb(0, 0, 0, 0);
            lcd.Grid = Argb(0, 0, 0, 0);
            lcd.Accent = Rgb(255, 176, 0);
            lcd.Link = Rgb(255, 176, 0);
            lcd.Series = (i, n) => Rgb(255, 176, 0);
            list.Add(lcd);

            // 9 · Floating
            Skin floating = Base();
            floating.Id = "floating";
            floating.Name = "Floating";
            floating.Blurb = "No panels at all: thin text and lines straight on the wallpaper, kept readable with a shadow.";
            floating.PanelStyle = PanelStyle.None;
            floating.BarStyle = BarStyle.Thin;
            floating.GraphStyle = GraphStyle.Line;
            floating.TextShadow = true;
            floating.Width = 176;
            floating.Pad = 6;
            floating.Row = 13;
            floating.Title = Rgb(244, 247, 250);
            floating.Fore = Rgb(236, 241, 246);
            floating.Dim = Rgb(201, 212, 222);
            floating.Faint = Rgb(185, 199, 212);
            floating.Rule = Argb(70, 255, 255, 255);
            floating.Track = Argb(50, 255, 255, 255);
            floating.GraphBack = Argb(0, 0, 0, 0);
            floating.Grid = Argb(0, 0, 0, 0);
            floating.Link = Rgb(143, 220, 255);
            list.Add(floating);

            // 10 · Taskbar Strip
            Skin strip = Base();
            strip.Id = "strip";
            strip.Name = "Taskbar Strip";
            strip.Blurb = "Short, wide panels that sit along the top or bottom edge of the screen and snap side by side.";
            strip.Layout = MeterLayout.Strip;
            strip.DefaultRows = RowMode.Equalizer;
            strip.Width = 340;
            strip.Pad = 12;
            strip.Radius = 9;
            strip.Shadow = true;
            list.Add(strip);

            return list;
        }
    }
}
