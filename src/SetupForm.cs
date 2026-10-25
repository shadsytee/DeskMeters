using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeskMeters
{
    /// <summary>The install / uninstall window. Dark, one page, a few checkboxes.</summary>
    internal sealed class SetupForm : Form
    {
        private static readonly Color Back = Color.FromArgb(30, 34, 41);
        private static readonly Color Line = Color.FromArgb(66, 74, 88);
        private static readonly Color Text1 = Color.FromArgb(238, 241, 245);
        private static readonly Color Text2 = Color.FromArgb(170, 180, 194);
        private static readonly Color Dim = Color.FromArgb(138, 150, 166);

        private readonly bool _uninstall;
        private readonly CheckBox _startup, _startMenu, _desktop, _launch, _deleteSettings;
        private readonly Label _status;
        private readonly Button _go, _cancel;
        private readonly ProgressBar _bar;
        private bool _busy;

        public SetupForm(bool uninstall)
        {
            _uninstall = uninstall;
            Text = uninstall ? "Uninstall DeskMeters" : "Install DeskMeters";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Back;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(480, uninstall ? 300 : 392);
            try { Icon = Glyphs.AppIcon(32); } catch { }

            var icon = new PictureBox { Location = new Point(22, 20), Size = new Size(40, 40), SizeMode = PictureBoxSizeMode.Zoom };
            try { icon.Image = Glyphs.AppIcon(64).ToBitmap(); } catch { }

            var title = new Label
            {
                Text = uninstall ? "Remove DeskMeters from this computer" : "DeskMeters " + MeterApp.Version,
                Location = new Point(72, 20),
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 13f),
            };
            var sub = new Label
            {
                Text = uninstall
                    ? "Stops the meters, removes the startup entry, the shortcuts and the program files."
                    : "CPU, GPU, Network and Drives meters for your desktop.\nInstalls for the current user only. No administrator rights are needed for the files.",
                Location = new Point(72, 46),
                Size = new Size(390, 36),
                ForeColor = Text2,
            };

            var rule = new Panel { Location = new Point(22, 92), Size = new Size(436, 1), BackColor = Line };

            int y = 104;
            if (!uninstall)
            {
                var where = new Label { Text = "Install to:", Location = new Point(22, y), AutoSize = true, ForeColor = Dim };
                var path = new Label { Text = Installer.InstallDir, Location = new Point(100, y), Size = new Size(360, 18), ForeColor = Text2, AutoEllipsis = true };
                Controls.Add(where);
                Controls.Add(path);
                y += 30;

                _startup = Check("Start with Windows, with temperatures enabled (recommended)", y, true);
                var note = new Label
                {
                    Text = "Creates a scheduled task that runs DeskMeters as administrator at sign-in, which is what the CPU and GPU temperature sensors need. You'll see one Windows security prompt now and never again.",
                    Location = new Point(44, y + 22),
                    Size = new Size(416, 44),
                    ForeColor = Dim,
                    Font = new Font("Segoe UI", 8.5f),
                };
                Controls.Add(note);
                y += 72;
                _startMenu = Check("Add a Start Menu shortcut", y, true); y += 26;
                _desktop = Check("Add a desktop shortcut", y, false); y += 26;
                _launch = Check("Start DeskMeters when installation finishes", y, true); y += 30;
            }
            else
            {
                _deleteSettings = Check("Also delete my settings and network totals", y, false);
                y += 40;
            }

            _bar = new ProgressBar { Location = new Point(22, y + 4), Size = new Size(436, 6), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Visible = false };
            _status = new Label { Location = new Point(22, y + 16), Size = new Size(436, 34), ForeColor = Text2 };
            y += 54;

            _go = MakeButton(uninstall ? "Uninstall" : "Install", new Point(ClientSize.Width - 22 - 88 - 8 - 96, y), 96, true);
            _cancel = MakeButton("Cancel", new Point(ClientSize.Width - 22 - 88, y), 88, false);
            _go.Click += async (s, e) => await GoAsync();
            _cancel.Click += (s, e) => { if (!_busy) Close(); };
            AcceptButton = _go;
            CancelButton = _cancel;

            ClientSize = new Size(480, y + 44);
            Controls.AddRange(new Control[] { icon, title, sub, rule, _bar, _status, _go, _cancel });

            if (!uninstall && Installer.IsInstalled)
                _status.Text = "DeskMeters is already installed. Installing again updates it and keeps your settings.";
        }

        private CheckBox Check(string text, int y, bool on)
        {
            var c = new CheckBox { Text = text, Location = new Point(24, y), AutoSize = true, Checked = on, ForeColor = Text1 };
            Controls.Add(c);
            return c;
        }

        private Button MakeButton(string text, Point at, int width, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Location = at,
                Size = new Size(width, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(46, 96, 150) : Color.FromArgb(44, 50, 60),
                ForeColor = Text1,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = primary ? Color.FromArgb(92, 200, 255) : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(56, 116, 180) : Color.FromArgb(58, 66, 80);
            return b;
        }

        private async Task GoAsync()
        {
            if (_busy) return;
            if (_go.Text == "Close") { Close(); return; }

            var o = new Installer.Options
            {
                StartWithWindows = _startup?.Checked ?? false,
                StartMenu = _startMenu?.Checked ?? false,
                Desktop = _desktop?.Checked ?? false,
                Launch = _launch?.Checked ?? false,
                DeleteSettings = _deleteSettings?.Checked ?? false,
            };

            _busy = true;
            _go.Enabled = false;
            _cancel.Enabled = false;
            _bar.Visible = true;
            foreach (Control c in Controls) if (c is CheckBox) c.Enabled = false;

            string error = null;
            try
            {
                await Task.Run(() =>
                {
                    if (_uninstall) Installer.Uninstall(o, Status);
                    else Installer.Install(o, Status);
                });
            }
            catch (Exception ex)
            {
                Log.Error(_uninstall ? "Uninstall" : "Install", ex);
                error = ex.Message;
            }

            _busy = false;
            _bar.Visible = false;
            if (error != null)
            {
                _status.ForeColor = Color.FromArgb(245, 156, 68);
                _status.Text = "Something went wrong: " + error;
                _cancel.Enabled = true;
                _go.Enabled = true;
                return;
            }

            _status.ForeColor = Color.FromArgb(120, 214, 140);
            _status.Text = _uninstall
                ? "DeskMeters has been removed."
                : "Installed. Right-click the tray icon for settings, or right-click any meter.";
            _go.Text = "Close";
            _go.Enabled = true;
        }

        private void Status(string text)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() => _status.Text = text));
        }
    }
}
