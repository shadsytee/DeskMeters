using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskMeters
{
    /// <summary>
    /// Font families for the skins. The bundled ones (Rajdhani, VT323) are loaded from
    /// embedded resources into private collections; anything else comes from Windows,
    /// with Segoe UI as the fallback.
    /// </summary>
    internal static class Fonts
    {
        private static readonly Dictionary<string, FontFamily> Private = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<PrivateFontCollection> Collections = new List<PrivateFontCollection>();
        private static readonly List<IntPtr> Pinned = new List<IntPtr>();
        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();
        private static readonly Dictionary<string, FontFamily> SystemFamilies = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        private static readonly (string resource, string family)[] Bundled =
        {
            ("fonts.Rajdhani-Medium.ttf", "Rajdhani Medium"),
            ("fonts.Rajdhani-SemiBold.ttf", "Rajdhani SemiBold"),
            ("fonts.VT323-Regular.ttf", "VT323"),
        };

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            // Preview builds on other platforms can't render private fonts; let them use installed copies.
            if (Environment.GetEnvironmentVariable("DESKMETERS_SYSTEM_FONTS") == "1") return;

            foreach (var (resource, family) in Bundled)
            {
                try
                {
                    using (Stream s = typeof(Fonts).Assembly.GetManifestResourceStream(resource))
                    {
                        if (s == null) continue;
                        var bytes = new byte[s.Length];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int n = s.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }

                        // One collection per file so each file is its own family and GDI+
                        // never has to pick between weights it doesn't understand.
                        IntPtr mem = Marshal.AllocHGlobal(bytes.Length);
                        Marshal.Copy(bytes, 0, mem, bytes.Length);
                        Pinned.Add(mem);
                        var pfc = new PrivateFontCollection();
                        pfc.AddMemoryFont(mem, bytes.Length);
                        Collections.Add(pfc);
                        if (pfc.Families.Length > 0)
                            Private[family] = pfc.Families[0];
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Font " + family, ex);
                }
            }
        }

        public static FontFamily Family(string name)
        {
            EnsureLoaded();
            if (Private.TryGetValue(name, out FontFamily pf)) return pf;
            if (SystemFamilies.TryGetValue(name, out FontFamily sf)) return sf;

            FontFamily found = null;
            try { found = new FontFamily(name); }
            catch { /* not installed */ }

            if (found == null && !name.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase))
            {
                try { found = new FontFamily("Segoe UI"); }
                catch { found = FontFamily.GenericSansSerif; }
            }
            SystemFamilies[name] = found ?? FontFamily.GenericSansSerif;
            return SystemFamilies[name];
        }

        /// <summary>Cached font in pixels; falls back to Regular if the family lacks the style.</summary>
        public static Font Get(string family, float px, FontStyle style = FontStyle.Regular)
        {
            px = (float)Math.Round(px * 4) / 4f;
            string key = family + "|" + px + "|" + (int)style;
            if (Cache.TryGetValue(key, out Font f)) return f;

            FontFamily fam = Family(family);
            FontStyle use = style;
            // Underline/strikeout are decorations, not faces; only the face part must exist.
            FontStyle face = use & (FontStyle.Bold | FontStyle.Italic);
            if (!fam.IsStyleAvailable(face))
            {
                face = FontStyle.Regular;
                if (!fam.IsStyleAvailable(face)) face = FontStyle.Bold;
                use = face | (use & (FontStyle.Underline | FontStyle.Strikeout));
            }
            try
            {
                f = new Font(fam, px, use, GraphicsUnit.Pixel);
            }
            catch
            {
                f = new Font(FontFamily.GenericSansSerif, px, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            Cache[key] = f;
            return f;
        }
    }
}
