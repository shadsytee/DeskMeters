using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using DeskMeters.Meters;

namespace DeskMeters
{
    /// <summary>
    /// Invisible foreground anchor for context menus, kept out of Alt+Tab. It also
    /// receives the "show yourself" message from a second instance.
    /// </summary>
    internal sealed class MenuHostForm : Form
    {
        public const string Caption = "DeskMeters.Host";

        public event Action ShowRequested;
        public event Action ExitRequested;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Let a non-elevated second instance message this (possibly elevated) one.
            try
            {
                Native.ChangeWindowMessageFilterEx(Handle, Native.WM_DESKMETERS_SHOW, Native.MSGFLT_ALLOW, IntPtr.Zero);
                Native.ChangeWindowMessageFilterEx(Handle, Native.WM_DESKMETERS_EXIT, Native.MSGFLT_ALLOW, IntPtr.Zero);
            }
            catch { /* older Windows */ }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DESKMETERS_SHOW)
            {
                ShowRequested?.Invoke();
                return;
            }
            if (m.Msg == Native.WM_DESKMETERS_EXIT)
            {
                ExitRequested?.Invoke();
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>App lifetime: tray icon, the four meters, the sampler and settings.</summary>
    internal sealed class MeterApp : ApplicationContext
    {
        public Settings Settings { get; } = new Settings();

        /// <summary>Network rates are shown with bits (kbit/s) as the primary unit.</summary>
        public static volatile bool RatesInBits = true;

        private readonly List<MeterWindow> _meters = new List<MeterWindow>();
        private readonly Control _invoker = new Control();
        private readonly NotifyIcon _tray;
        private readonly Sampler _sampler;
        private readonly Timer _desktopWatch = new Timer { Interval = 350 };
        private readonly Timer _saveTimer = new Timer { Interval = 60_000 };
        private readonly Timer _unraiseTimer = new Timer { Interval = 5000 };
        private readonly Timer _restoreTimer = new Timer { Interval = 3000 };
        private int _restoreTicks;
        private readonly HashSet<string> _needsDefaultPosition = new HashSet<string>();
        private bool _desktopShown;
        private bool _fullscreenActive;
        private bool _fullscreenHidden;
        private bool _briefRaise;
        private bool _defaultsApplied;
        private bool _exiting;
        private bool? _autostart;
        private MenuHostForm _menuHost;
        private bool _forceRoomy;               // upgrading: keep the old size until the layout is migrated
        private bool _densityMigrationPending;
        private readonly DateTime _startedAt = DateTime.Now;

        public MeterApp()
        {
            _invoker.CreateControl();

            Fmt.Fahrenheit = Settings.GetBool("fahrenheit", false);
            RatesInBits = Settings.GetBool("net.bits", true);
            Theme.SharpTextEnabled = Settings.GetBool("sharptext", true);

            _meters.Add(new CpuMeter(this));
            _meters.Add(new GpuMeter(this));
            _meters.Add(new NetworkMeter(this));
            _meters.Add(new DrivesMeter(this));

            _tray = new NotifyIcon
            {
                Icon = Glyphs.AppIcon(Math.Max(16, SystemInformation.SmallIconSize.Width)),
                Text = "DeskMeters",
                Visible = true,
            };
            _tray.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left)
                    ShowTrayMenu();
            };
            _tray.DoubleClick += (s, e) => RaiseAllBriefly();

            EnsureMenuHost();

            _sampler = new Sampler(Settings, snap =>
            {
                if (_invoker.IsHandleCreated && !_exiting)
                    _invoker.BeginInvoke(new Action(() => OnSnapshot(snap)));
            });
            ApplySamplerOptions();

            // Coming from a version without the compact layout: start at the old size so the
            // saved layout comes up exactly as it was, then shrink and tidy it once the
            // sensors are up and every meter has its real height.
            if (!Settings.Has("compact") && _meters.Any(m => m.HasSavedPosition))
            {
                _densityMigrationPending = true;
                _forceRoomy = true;
            }

            foreach (MeterWindow m in _meters)
            {
                if (!Settings.GetBool(m.Key + ".visible", true)) continue;
                ShowMeter(m);
            }
            ApplyClickThrough();

            _desktopWatch.Tick += (s, e) => WatchDesktop();
            _desktopWatch.Start();
            _saveTimer.Tick += (s, e) => Settings.Save();
            _saveTimer.Start();
            _unraiseTimer.Tick += (s, e) =>
            {
                _unraiseTimer.Stop();
                _briefRaise = false;
                UpdateRaise();
            };

            _sampler.Start();

            // For two minutes after start-up, keep trying to put displaced meters back:
            // at sign-in a second monitor can show up after we've started.
            _restoreTimer.Tick += (s, e) =>
            {
                RestoreDisplaced();
                if (++_restoreTicks >= 40 || !_meters.Any(m => m.Displaced)) _restoreTimer.Stop();
            };
            if (_meters.Any(m => m.Displaced)) _restoreTimer.Start();

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            System.Threading.Tasks.Task.Run(() =>
            {
                bool on = Autostart.IsEnabled();
                if (_invoker.IsHandleCreated && !_exiting)
                    _invoker.BeginInvoke(new Action(() => { if (_autostart == null) _autostart = on; }));
            });

            if (!_meters.Any(m => m.Visible))
                _tray.ShowBalloonTip(4000, "DeskMeters", "All meters are hidden. Click the tray icon to show them.", ToolTipIcon.Info);

            Log.Info($"Started v{Version}. Admin={Sampler.IsAdmin}, design={DesignId}");
        }

        public static string Version
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : "");
            }
        }

        public IEnumerable<MeterWindow> VisibleMeters => _meters.Where(m => m.Visible);

        public bool Locked
        {
            get => Settings.GetBool("locked", false);
            set => Settings.SetBool("locked", value);
        }

        public bool SnapEnabled
        {
            get => Settings.GetBool("snap", true);
            set => Settings.SetBool("snap", value);
        }

        /// <summary>Gadget-sized meters (narrower, tighter rows). On by default.</summary>
        public bool Compact
        {
            get => !_forceRoomy && Settings.GetBool("compact", true);
            set => SetDensity(value);
        }

        /// <summary>ClearType text on designs with an opaque panel.</summary>
        public bool SharpText
        {
            get => Settings.GetBool("sharptext", true);
            set
            {
                Settings.SetBool("sharptext", value);
                Theme.SharpTextEnabled = value;
                foreach (MeterWindow m in _meters) m.Render();
                Settings.Save();
            }
        }

        private void SetDensity(bool compact)
        {
            if (_densityMigrationPending)
            {
                _densityMigrationPending = false;
                _forceRoomy = false;
            }
            Dictionary<MeterWindow, Rectangle> before = CaptureBounds();
            Settings.SetBool("compact", compact);
            foreach (MeterWindow m in _meters) m.Render();
            Reflow(before);
            Settings.Save();
        }

        private void MigrateDensity()
        {
            _densityMigrationPending = false;
            _forceRoomy = true;
            foreach (MeterWindow m in _meters) if (m.Visible) m.Render();
            Dictionary<MeterWindow, Rectangle> before = CaptureBounds();
            _forceRoomy = false;
            Settings.SetBool("compact", true);
            foreach (MeterWindow m in _meters) m.Render();
            Reflow(before);
            Settings.Save();
            Log.Info("Switched the saved layout to the compact size");
        }

        private Dictionary<MeterWindow, Rectangle> CaptureBounds()
        {
            var d = new Dictionary<MeterWindow, Rectangle>();
            foreach (MeterWindow m in _meters)
                if (m.Visible && !m.Displaced && !m.Provisional && m.HasSavedPosition)
                    d[m] = m.PanelBounds;
            return d;
        }

        /// <summary>
        /// After the meters change size, keep the arrangement the user made: meters on the
        /// right half of a screen keep their right edge, those on the left keep their left
        /// edge, and meters that were stacked or side by side stay that way with the same gaps.
        /// </summary>
        private void Reflow(Dictionary<MeterWindow, Rectangle> before)
        {
            if (before.Count == 0) return;
            int tol = (int)Math.Round(16 * Native.DpiFor(_invoker.Handle) / 96.0);
            var size = before.Keys.ToDictionary(m => m, m => m.PanelBounds.Size);
            var newX = new Dictionary<MeterWindow, int>();
            var newY = new Dictionary<MeterWindow, int>();

            bool RightHalf(MeterWindow m)
            {
                Rectangle o = before[m];
                Rectangle wa = Screen.FromRectangle(o).WorkingArea;
                return o.Left + o.Width / 2 > wa.Left + wa.Width / 2;
            }
            bool OverlapY(Rectangle a, Rectangle b) => a.Top < b.Bottom && b.Top < a.Bottom;
            bool OverlapX(Rectangle a, Rectangle b) => a.Left < b.Right && b.Left < a.Right;

            // Horizontal: left-anchored meters from the left, right-anchored from the right.
            foreach (MeterWindow m in before.Keys.Where(k => !RightHalf(k)).OrderBy(k => before[k].Left).ThenBy(k => before[k].Top))
            {
                Rectangle o = before[m];
                int x = o.Left;
                MeterWindow left = newX.Keys.Where(a => OverlapY(before[a], o) && o.Left - before[a].Right >= 0 && o.Left - before[a].Right <= tol)
                                            .OrderByDescending(a => before[a].Right).FirstOrDefault();
                MeterWindow above = newX.Keys.Where(a => OverlapX(before[a], o) && Math.Abs(before[a].Left - o.Left) <= 2).FirstOrDefault();
                if (left != null) x = newX[left] + size[left].Width + (o.Left - before[left].Right);
                else if (above != null) x = newX[above];
                newX[m] = x;
            }
            var placedRight = new HashSet<MeterWindow>();
            foreach (MeterWindow m in before.Keys.Where(RightHalf).OrderByDescending(k => before[k].Right).ThenBy(k => before[k].Top))
            {
                Rectangle o = before[m];
                int right = o.Right;
                MeterWindow rightOf = placedRight.Where(a => OverlapY(before[a], o) && before[a].Left - o.Right >= 0 && before[a].Left - o.Right <= tol)
                                                 .OrderBy(a => before[a].Left).FirstOrDefault();
                MeterWindow above = placedRight.Where(a => OverlapX(before[a], o) && Math.Abs(before[a].Right - o.Right) <= 2).FirstOrDefault();
                if (rightOf != null) right = newX[rightOf] - (before[rightOf].Left - o.Right);
                else if (above != null) right = newX[above] + size[above].Width;
                newX[m] = right - size[m].Width;
                placedRight.Add(m);
            }

            // Vertical: close up gaps under meters that got shorter (or make room if taller).
            foreach (MeterWindow m in before.Keys.OrderBy(k => before[k].Top).ThenBy(k => before[k].Left))
            {
                Rectangle o = before[m];
                int y = o.Top;
                MeterWindow above = newY.Keys.Where(a => OverlapX(before[a], o) && o.Top - before[a].Bottom >= 0 && o.Top - before[a].Bottom <= tol)
                                             .OrderByDescending(a => before[a].Bottom).FirstOrDefault();
                if (above != null) y = newY[above] + size[above].Height + (o.Top - before[above].Bottom);
                newY[m] = y;
            }

            foreach (MeterWindow m in before.Keys)
            {
                var p = new Point(newX[m], newY[m]);
                if (p == before[m].Location) continue;
                m.MovePanelTo(p);
                m.SavePosition();
            }
        }

        public bool ClickThrough
        {
            get => Settings.GetBool("clickthrough", false);
            set { Settings.SetBool("clickthrough", value); ApplyClickThrough(); }
        }

        /// <summary>What to do while a full-screen app (game, video) is active: "top", "hide" or "none".</summary>
        public string FullscreenMode
        {
            get
            {
                string v = Settings.Get("fullscreen");
                if (v == null) v = Settings.GetBool("hidefullscreen", false) ? "hide" : "top";
                return v;
            }
            set
            {
                Settings.Set("fullscreen", value);
                Settings.Set("hidefullscreen", null);
                _fullscreenActive = false;   // re-evaluate on the next tick
                if (_fullscreenHidden)
                {
                    _fullscreenHidden = false;
                    foreach (MeterWindow m in _meters)
                        if (Settings.GetBool(m.Key + ".visible", true)) { m.Show(); m.Render(); }
                }
                UpdateRaise();
            }
        }

        /// <summary>The design every meter follows unless it has its own override.</summary>
        public string DesignId
        {
            get
            {
                string id = Settings.Get("design", Skins.DefaultId);
                return Skins.All.Any(s => s.Id == id) ? id : Skins.DefaultId;
            }
            set
            {
                Settings.Set("design", value);
                foreach (MeterWindow m in _meters)
                {
                    m.DesignOverride = null;   // choosing a global design resets per-meter ones
                    m.Render();
                }
                Settings.Save();
            }
        }

        // ---- meters ---------------------------------------------------------------------

        private void ShowMeter(MeterWindow m)
        {
            if (m.HasSavedPosition)
            {
                m.RestorePosition();
                if (!OnAnyScreen(m.Bounds))
                {
                    // Its monitor isn't there right now (still waking up at sign-in, or
                    // unplugged). Show it on the main monitor for now, but keep the saved
                    // position so it goes back as soon as that monitor appears.
                    m.Displaced = true;
                    _needsDefaultPosition.Add(m.Key);
                }
            }
            else
            {
                m.Provisional = true;   // brand new: placed by us until the user moves it
                _needsDefaultPosition.Add(m.Key);
            }

            if (_needsDefaultPosition.Contains(m.Key))
                m.Location = new Point(-32000, -32000); // parked until real sizes are known

            m.Show();
            m.Render(); // size the window now; OnShown arrives asynchronously
            if (_needsDefaultPosition.Contains(m.Key) && _defaultsApplied)
                PlaceDefaults();
        }

        private static bool OnAnyScreen(Rectangle r)
        {
            // At least a 40×40 corner must be on a screen so it can be grabbed.
            var grab = new Rectangle(r.X, r.Y, Math.Min(40, r.Width), Math.Min(40, r.Height));
            return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(grab));
        }

        private Rectangle SavedBounds(MeterWindow m)
        {
            Size sz = m.PixelSize.IsEmpty ? m.Size : m.PixelSize;
            return new Rectangle(Settings.GetInt(m.Key + ".x", 0), Settings.GetInt(m.Key + ".y", 0), sz.Width, sz.Height);
        }

        /// <summary>
        /// Put meters back where the user left them once their monitor is available again.
        /// Called when the display setup changes, and every few seconds for the first couple
        /// of minutes after start-up (monitors often appear after we've started at sign-in).
        /// </summary>
        private void RestoreDisplaced()
        {
            foreach (MeterWindow m in _meters)
            {
                if (!m.Displaced || !m.HasSavedPosition) continue;
                if (!OnAnyScreen(SavedBounds(m))) continue;
                m.Displaced = false;
                m.RestorePosition();
                m.Render();
                m.ApplyZOrder();
                Log.Info($"Restored {m.Key} to its saved position");
            }
        }

        /// <summary>
        /// Stack un-positioned meters down the right edge of the primary screen (or, for the
        /// Taskbar Strip design, along the top edge). Only brand-new meters get their new
        /// position saved; a displaced meter keeps its saved spot for when its monitor returns.
        /// </summary>
        private void PlaceDefaults()
        {
            _defaultsApplied = true;
            if (_needsDefaultPosition.Count == 0) return;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int gap = (int)Math.Round(8 * Native.DpiFor(_invoker.Handle) / 96.0);
            bool strip = Skins.Get(DesignId).Layout == MeterLayout.Strip;

            // Start beside meters that are already sitting on the primary screen's edge,
            // so placed and restored meters don't overlap.
            int x = strip ? wa.Left + gap : wa.Right - gap;
            int y = wa.Top + gap;
            int colWidth = 0;

            foreach (MeterWindow m in _meters)
            {
                if (!m.Visible || !_needsDefaultPosition.Contains(m.Key)) continue;
                Size sz = m.PanelBounds.Size; // shadows may overlap; panels don't
                if (strip)
                {
                    if (x + sz.Width > wa.Right - gap && x > wa.Left + gap)
                    {
                        x = wa.Left + gap;
                        y += sz.Height + gap;
                    }
                    m.MovePanelTo(new Point(x, y));
                    x += sz.Width + gap;
                }
                else
                {
                    if (y + sz.Height > wa.Bottom - gap && y > wa.Top + gap)
                    {
                        x -= colWidth + gap;
                        y = wa.Top + gap;
                        colWidth = 0;
                    }
                    m.MovePanelTo(new Point(x - sz.Width, y));
                    y += sz.Height + gap;
                    colWidth = Math.Max(colWidth, sz.Width);
                }
                if (!m.Displaced) m.SavePosition();
                m.ApplyZOrder();
            }

            _needsDefaultPosition.Clear();
            Settings.Save();
        }

        public void SetMeterVisible(MeterWindow m, bool visible)
        {
            Settings.SetBool(m.Key + ".visible", visible);
            if (visible && !m.Visible)
                ShowMeter(m);
            else if (!visible && m.Visible)
                m.Hide();
            ApplySamplerOptions();
            Settings.Save();

            if (!visible && !_meters.Any(x => x.Visible))
                _tray.ShowBalloonTip(3000, "DeskMeters", "Everything is hidden. Click the tray icon to bring a meter back.", ToolTipIcon.Info);
        }

        public void CopyLookToAll(MeterWindow source)
        {
            foreach (MeterWindow m in _meters)
            {
                if (m == source) continue;
                Settings.SetInt(m.Key + ".size", source.SizePct);
                Settings.SetInt(m.Key + ".opacity", source.OpacityPct);
                Settings.Set(m.Key + ".z", source.ZMode.ToString());
                m.Render();
                m.ApplyZOrder();
            }
            Settings.Save();
        }

        private void SetAll(Action<MeterWindow> change)
        {
            foreach (MeterWindow m in _meters) change(m);
            Settings.Save();
        }

        private void ApplyClickThrough()
        {
            bool on = ClickThrough;
            foreach (MeterWindow m in _meters) m.SetClickThrough(on);
        }

        public void ApplySamplerOptions()
        {
            _sampler.IntervalMs = Settings.GetInt("interval", 1000);
            _sampler.PreferredAdapterId = Settings.Get("net.adapter");
            _sampler.PreferredGpu = Settings.Get("gpu.name");
            _sampler.IncludeNetworkDrives = Settings.GetBool("drives.network", true);
            _sampler.WantCpu = Settings.GetBool("cpu.visible", true);
            _sampler.WantGpu = Settings.GetBool("gpu.visible", true);
            _sampler.WantNet = Settings.GetBool("net.visible", true);
            _sampler.WantDrives = Settings.GetBool("drives.visible", true);
            _sampler.Poke();
        }

        public void SetAdapter(string id)
        {
            Settings.Set("net.adapter", id);
            ApplySamplerOptions();
        }

        public void SetGpu(string name)
        {
            Settings.Set("gpu.name", name);
            ApplySamplerOptions();
        }

        public void RecheckNetwork()
        {
            if (_sampler.NetServices != null) _sampler.NetServices.RefreshRequested = true;
        }

        public void AddNetworkLocation()
        {
            using (var dlg = new AddLocationForm())
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                List<Sources.NetLocation> list = Sources.NetworkLocations.LoadCustom(Settings);
                string key = dlg.ResultPath.TrimEnd('\\').ToLowerInvariant();
                list.RemoveAll(l => l.Key == key);
                list.Add(new Sources.NetLocation { Path = dlg.ResultPath, Name = dlg.ResultName, Custom = true });
                Sources.NetworkLocations.SaveCustom(Settings, list);
                Settings.Save();
            }
            _sampler.Locations?.Poke();
            _sampler.Poke();
        }

        public void RemoveNetworkLocation(string path)
        {
            List<Sources.NetLocation> list = Sources.NetworkLocations.LoadCustom(Settings);
            string key = path.TrimEnd('\\').ToLowerInvariant();
            list.RemoveAll(l => l.Key == key);
            Sources.NetworkLocations.SaveCustom(Settings, list);
            Settings.Save();
            _sampler.Locations?.Poke();
            _sampler.Poke();
        }

        public void ResetNetworkTotals()
        {
            if (_sampler.Network != null) _sampler.Network.ResetTotalsRequested = true;
            _sampler.Poke();
        }

        private void OnSnapshot(Snapshot snap)
        {
            if (_exiting) return;
            NoteSnapshot(snap);
            foreach (MeterWindow m in _meters)
            {
                try { m.Feed(snap); }
                catch (Exception ex) { Log.Error(m.Key + ".Feed", ex); }
            }

            if (_densityMigrationPending && (snap.SensorsReady || (DateTime.Now - _startedAt).TotalSeconds > 30))
            {
                try { MigrateDensity(); }
                catch (Exception ex) { Log.Error("Compact layout migration", ex); _forceRoomy = false; }
            }

            // Meters without a saved position (first run, or after Reset positions) are laid
            // out as soon as there's anything to show, then once more when the sensors are up
            // and they have their final heights, unless the user has moved one by then.
            // Meters with a saved position are never touched here.
            if (!_defaultsApplied)
            {
                PlaceDefaults();
            }
            else if (snap.SensorsReady && _meters.Any(m => m.Provisional))
            {
                foreach (MeterWindow m in _meters)
                {
                    if (!m.Provisional) continue;
                    m.Provisional = false;
                    if (m.Visible)
                    {
                        Settings.Set(m.Key + ".x", null);
                        Settings.Set(m.Key + ".y", null);
                        _needsDefaultPosition.Add(m.Key);
                    }
                }
                PlaceDefaults();
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (_invoker.InvokeRequired)
            {
                _invoker.BeginInvoke(new Action(() => OnDisplaySettingsChanged(sender, e)));
                return;
            }

            // A monitor came back: return meters to where they were saved.
            RestoreDisplaced();

            // A monitor went away: show stranded meters on the main monitor for now, without
            // forgetting where they belong.
            foreach (MeterWindow m in _meters)
            {
                if (!m.Visible) continue;
                m.Render();
                if (!OnAnyScreen(m.Bounds))
                {
                    m.Displaced = true;
                    _needsDefaultPosition.Add(m.Key);
                }
            }
            if (_needsDefaultPosition.Count > 0) PlaceDefaults();
        }

        // ---- z-order: keep "on desktop" meters visible while the desktop is shown --------

        private void WatchDesktop()
        {
            IntPtr fg = Native.GetForegroundWindow();
            string cls = fg == IntPtr.Zero ? "" : Native.ClassNameOf(fg);
            bool desktop = cls == "WorkerW" || cls == "Progman";

            // A window covering a whole monitor (not just the working area) that isn't merely
            // maximised is a full-screen game or video.
            bool fullscreen = false;
            if (fg != IntPtr.Zero && !desktop && cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd" && cls != "Windows.UI.Core.CoreWindow")
            {
                if (Native.GetWindowRect(fg, out Native.RECT r))
                {
                    Rectangle wr = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    Screen sc = Screen.FromRectangle(wr);
                    fullscreen = wr.Left <= sc.Bounds.Left && wr.Top <= sc.Bounds.Top &&
                                 wr.Right >= sc.Bounds.Right && wr.Bottom >= sc.Bounds.Bottom && !Native.IsZoomed(fg);
                }
            }

            bool changed = fullscreen != _fullscreenActive || desktop != _desktopShown;
            _fullscreenActive = fullscreen;
            _desktopShown = desktop;
            if (!changed) return;

            // "hide": get out of the way entirely.
            bool hide = fullscreen && FullscreenMode == "hide";
            if (hide != _fullscreenHidden)
            {
                _fullscreenHidden = hide;
                foreach (MeterWindow m in _meters)
                {
                    if (!Settings.GetBool(m.Key + ".visible", true)) continue;
                    if (hide) m.Hide();
                    else { m.Show(); m.Render(); m.ApplyZOrder(); }
                }
            }

            UpdateRaise();
        }

        /// <summary>
        /// Decide which meters sit above everything right now: all of them for a few seconds
        /// after a double-click, "on the desktop" ones while the desktop is shown, and all of
        /// them while a full-screen app is active (unless that's turned off), so games get
        /// the meters laid over them like the old sidebar gadgets.
        /// </summary>
        private void UpdateRaise()
        {
            bool overGame = _fullscreenActive && FullscreenMode == "top";
            foreach (MeterWindow m in _meters)
                m.SetRaised(_briefRaise || overGame || (_desktopShown && m.ZMode == ZMode.Desktop));
        }

        public void RaiseAllBriefly()
        {
            if (_fullscreenHidden) return;
            _briefRaise = true;
            UpdateRaise();
            _unraiseTimer.Stop();
            _unraiseTimer.Start();
        }

        // ---- tray menu ------------------------------------------------------------------

        public void ShowTrayMenu()
        {
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
            menu.Items.Add(new ToolStripLabel("DeskMeters " + Version) { ForeColor = Color.FromArgb(138, 150, 166), Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(new ToolStripSeparator());

            // Meters
            foreach (MeterWindow m in _meters)
            {
                MeterWindow meter = m;
                menu.Items.Add(Item("Show " + m.Title + " meter", Settings.GetBool(m.Key + ".visible", true), () => SetMeterVisible(meter, !Settings.GetBool(meter.Key + ".visible", true))));
            }
            menu.Items.Add(new ToolStripSeparator());

            // Design
            var design = new ToolStripMenuItem("Design");
            foreach (Skin sk in Skins.All)
            {
                string id = sk.Id;
                var item = Item(sk.Name, DesignId == id && !_meters.Any(m => m.DesignOverride != null), () => DesignId = id);
                item.ToolTipText = sk.Blurb;
                design.DropDownItems.Add(item);
            }
            if (_meters.Any(m => m.DesignOverride != null))
            {
                design.DropDownItems.Add(new ToolStripSeparator());
                design.DropDownItems.Add(Item("Some meters use their own design (right-click one to change it)", false, () => { }));
            }
            menu.Items.Add(design);

            // Look of all meters
            var position = new ToolStripMenuItem("Position of all meters");
            position.DropDownItems.Add(Item("On the desktop (behind windows)", _meters.All(m => m.ZMode == ZMode.Desktop), () => SetAll(m => m.ZMode = ZMode.Desktop)));
            position.DropDownItems.Add(Item("Normal windows", _meters.All(m => m.ZMode == ZMode.Normal), () => SetAll(m => m.ZMode = ZMode.Normal)));
            position.DropDownItems.Add(Item("Always on top", _meters.All(m => m.ZMode == ZMode.Topmost), () => SetAll(m => m.ZMode = ZMode.Topmost)));
            menu.Items.Add(position);

            var size = new ToolStripMenuItem("Size of all meters");
            foreach (int pct in new[] { 70, 80, 90, 100, 110, 125, 150, 175, 200 })
            {
                int p = pct;
                size.DropDownItems.Add(Item(p + "%", _meters.All(m => m.SizePct == p), () => SetAll(m => m.SizePct = p)));
            }
            menu.Items.Add(size);

            var opacity = new ToolStripMenuItem("Opacity of all meters");
            foreach (int pct in new[] { 100, 90, 80, 70, 60, 50, 40, 30 })
            {
                int p = pct;
                opacity.DropDownItems.Add(Item(p + "%", _meters.All(m => m.OpacityPct == p), () => SetAll(m => m.OpacityPct = p)));
            }
            menu.Items.Add(opacity);
            menu.Items.Add(Item("Compact layout (gadget-sized)", Compact, () => Compact = !Compact));
            menu.Items.Add(Item("Sharper text (ClearType)", SharpText, () => SharpText = !SharpText));
            menu.Items.Add(new ToolStripSeparator());

            // Behaviour
            var interval = new ToolStripMenuItem("Update every");
            foreach (int ms in new[] { 500, 1000, 2000, 3000, 5000 })
            {
                int v = ms;
                interval.DropDownItems.Add(Item(ms < 1000 ? "½ second" : (ms / 1000) + (ms == 1000 ? " second" : " seconds"),
                                                Settings.GetInt("interval", 1000) == v,
                                                () => { Settings.SetInt("interval", v); ApplySamplerOptions(); }));
            }
            menu.Items.Add(interval);

            var units = new ToolStripMenuItem("Units");
            units.DropDownItems.Add(Item("Temperatures in °C", !Fmt.Fahrenheit, () => SetFahrenheit(false)));
            units.DropDownItems.Add(Item("Temperatures in °F", Fmt.Fahrenheit, () => SetFahrenheit(true)));
            units.DropDownItems.Add(new ToolStripSeparator());
            units.DropDownItems.Add(Item("Network speed in bits (kbit/s, Mbit/s)", RatesInBits, () => SetRatesInBits(true)));
            units.DropDownItems.Add(Item("Network speed in bytes (KB/s, MB/s)", !RatesInBits, () => SetRatesInBits(false)));
            menu.Items.Add(units);

            menu.Items.Add(Item("Lock positions", Locked, () => Locked = !Locked));
            menu.Items.Add(Item("Snap to edges and other meters", SnapEnabled, () => SnapEnabled = !SnapEnabled));
            menu.Items.Add(Item("Click-through (mouse ignores the meters)", ClickThrough, () => ClickThrough = !ClickThrough));
            var games = new ToolStripMenuItem("While a full-screen app or game is running");
            games.DropDownItems.Add(Item("Keep the meters on top of it", FullscreenMode == "top", () => FullscreenMode = "top"));
            games.DropDownItems.Add(Item("Hide the meters", FullscreenMode == "hide", () => FullscreenMode = "hide"));
            games.DropDownItems.Add(Item("Do nothing special", FullscreenMode == "none", () => FullscreenMode = "none"));
            menu.Items.Add(games);
            menu.Items.Add(Item("Bring all to front for 5 seconds", false, RaiseAllBriefly));
            menu.Items.Add(Item("Reset positions", false, ResetPositions));
            menu.Items.Add(new ToolStripSeparator());

            // System
            if (_autostart == null) _autostart = Autostart.IsEnabled();
            menu.Items.Add(Item("Start with Windows" + (Sampler.IsAdmin ? "" : " (with temperatures)"), _autostart == true, ToggleAutostart));
            if (!Sampler.IsAdmin)
                menu.Items.Add(Item("Restart as administrator (for temperatures)", false, RestartElevated));
            menu.Items.Add(Item("Open settings folder", false, () => OpenUrl(Log.Folder)));
            menu.Items.Add(Item("Reset all settings…", false, ResetAllSettings));
            menu.Items.Add(Item("About DeskMeters", false, ShowAbout));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", false, Exit));

            ShowMenu(menu, Cursor.Position);
        }

        private void SetFahrenheit(bool on)
        {
            Fmt.Fahrenheit = on;
            Settings.SetBool("fahrenheit", on);
            foreach (MeterWindow m in _meters) m.Render();
        }

        private void SetRatesInBits(bool on)
        {
            RatesInBits = on;
            Settings.SetBool("net.bits", on);
            foreach (MeterWindow m in _meters) m.Render();
        }

        private void EnsureMenuHost()
        {
            if (_menuHost != null && !_menuHost.IsDisposed) return;
            _menuHost = new MenuHostForm
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(1, 1),
                Text = MenuHostForm.Caption,
            };
            _menuHost.ShowRequested += () =>
            {
                // A second copy was launched: make sure something is visible.
                if (!_meters.Any(m => Settings.GetBool(m.Key + ".visible", true)))
                    SetMeterVisible(_meters[0], true);
                RaiseAllBriefly();
                _tray.ShowBalloonTip(2500, "DeskMeters", "DeskMeters is already running. Right-click the tray icon for settings.", ToolTipIcon.Info);
            };
            _menuHost.ExitRequested += Exit;
            _menuHost.Show();
        }

        /// <summary>
        /// Show a context menu from a window that never takes focus (the meters, the tray).
        /// A hidden host window is made foreground first; otherwise the menu wouldn't close
        /// when you click somewhere else.
        /// </summary>
        public void ShowMenu(ContextMenuStrip menu, Point at)
        {
            EnsureMenuHost();
            Native.SetForegroundWindow(_menuHost.Handle);
            menu.Closed += (s, e) => _invoker.BeginInvoke(new Action(menu.Dispose));
            menu.Show(at);
        }

        private static ToolStripMenuItem Item(string text, bool isChecked, Action click) => MeterWindow.Check(text, isChecked, click);

        private void ResetPositions()
        {
            foreach (MeterWindow m in _meters)
            {
                Settings.Set(m.Key + ".x", null);
                Settings.Set(m.Key + ".y", null);
                m.Displaced = false;
                if (m.Visible) _needsDefaultPosition.Add(m.Key);
            }
            PlaceDefaults();
        }

        private void ResetAllSettings()
        {
            DialogResult r = MessageBox.Show(
                "Reset every DeskMeters setting (design, positions, sizes, network locations, totals) and restart?",
                "DeskMeters", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, MessageBoxOptions.DefaultDesktopOnly);
            if (r != DialogResult.OK) return;

            _exiting = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _desktopWatch.Stop();
            _saveTimer.Stop();
            _sampler.Dispose();
            try { System.IO.File.Delete(Settings.FilePath); }
            catch (Exception ex) { Log.Error("Reset settings", ex); }

            string exe = Process.GetCurrentProcess().MainModule.FileName;
            try { Process.Start(new ProcessStartInfo(exe, "--after " + Process.GetCurrentProcess().Id) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Restart", ex); }
            _tray.Visible = false;
            ExitThread();
        }

        private void ShowAbout()
        {
            string exe = "";
            try { exe = Process.GetCurrentProcess().MainModule.FileName; } catch { }
            string text =
                "DeskMeters " + Version + "\n" +
                "Desktop CPU, GPU, Network and Drives meters.\n\n" +
                "Running as administrator: " + (Sampler.IsAdmin ? "yes" : "no (temperatures need it)") + "\n" +
                "PawnIO driver (CPU temperatures): " + (_lastPawnIo == null ? "unknown yet" : _lastPawnIo == true ? "installed" : "not installed") + "\n" +
                "Design: " + Skins.Get(DesignId).Name + "\n" +
                "Program: " + exe + "\n" +
                "Settings: " + Settings.FilePath + "\n\n" +
                "Sensors by LibreHardwareMonitor (MPL-2.0). Fonts Rajdhani and VT323 (OFL).";
            MessageBox.Show(text, "About DeskMeters", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        }

        private bool? _lastPawnIo;

        private void ToggleAutostart()
        {
            bool target = _autostart != true;
            bool ok = target ? Autostart.Enable() : Autostart.Disable();
            _autostart = Autostart.IsEnabled();
            if (!ok && _autostart != target)
                _tray.ShowBalloonTip(3000, "DeskMeters", "Couldn't change the startup task (UAC cancelled?).", ToolTipIcon.Warning);
            else if (target)
                _tray.ShowBalloonTip(3000, "DeskMeters", "DeskMeters will start at sign-in, with temperatures enabled.", ToolTipIcon.Info);
        }

        // ---- actions --------------------------------------------------------------------

        public void OpenUrl(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("Open " + target, ex);
            }
        }

        public void OpenLog() => OpenUrl(Log.Folder);

        public void RestartElevated()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            var psi = new ProcessStartInfo(exe, "--after " + Process.GetCurrentProcess().Id)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            try
            {
                Process.Start(psi);
            }
            catch (Win32Exception)
            {
                return; // UAC cancelled: keep running as we are
            }
            Exit();
        }

        public void Exit()
        {
            if (_exiting) return;
            _exiting = true;

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _desktopWatch.Stop();
            _saveTimer.Stop();
            _unraiseTimer.Stop();
            _restoreTimer.Stop();
            _sampler.Dispose();          // persists network totals
            foreach (MeterWindow m in _meters)
                if (m.Visible && !m.Displaced && !m.Provisional && OnAnyScreen(m.Bounds)) m.SavePosition();
            Settings.Save(force: true);

            _tray.Visible = false;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_exiting) Exit();
                _tray.Dispose();
                foreach (MeterWindow m in _meters) m.Dispose();
                _invoker.Dispose();
                _menuHost?.Dispose();
                _desktopWatch.Dispose();
                _saveTimer.Dispose();
                _unraiseTimer.Dispose();
                _restoreTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Remember what the sampler found out, for the About box.</summary>
        internal void NoteSnapshot(Snapshot snap)
        {
            if (snap.SensorsReady) _lastPawnIo = snap.PawnIoInstalled;
        }
    }
}
