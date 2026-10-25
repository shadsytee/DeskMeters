using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeskMeters.Sources;

namespace DeskMeters
{
    /// <summary>Small dark dialog for adding a network share or mapped drive to the Drives meter.</summary>
    internal sealed class AddLocationForm : Form
    {
        private static readonly Color Back = Color.FromArgb(30, 34, 41);
        private static readonly Color Field = Color.FromArgb(20, 23, 28);
        private static readonly Color Line = Color.FromArgb(66, 74, 88);

        private readonly TextBox _path;
        private readonly TextBox _name;
        private readonly Label _status;
        private readonly Button _add;
        private bool _confirmUnreachable;

        public string ResultPath { get; private set; }
        public string ResultName { get; private set; }

        public AddLocationForm()
        {
            Text = "Add network location";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Back;
            ForeColor = Theme.Fore;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(420, 238);
            Padding = new Padding(16);
            try { Icon = Glyphs.AppIcon(32); } catch { }

            var intro = new Label
            {
                Text = "A shared folder (\\\\server\\share) or a mapped drive letter (Z:). " +
                       "It appears in the Drives meter with its free space and, where Windows reports it, its activity.",
                Location = new Point(16, 14),
                Size = new Size(388, 36),
                ForeColor = Theme.Dim,
            };

            var pathLabel = new Label { Text = "Path", Location = new Point(16, 58), AutoSize = true, ForeColor = Theme.Dim };
            _path = MakeBox(new Point(16, 78), 300);
            var browse = MakeButton("Browse…", new Point(324, 77), 80, false);
            browse.Click += (s, e) => Browse();

            var nameLabel = new Label { Text = "Name (optional)", Location = new Point(16, 112), AutoSize = true, ForeColor = Theme.Dim };
            _name = MakeBox(new Point(16, 132), 388);

            _status = new Label { Location = new Point(16, 166), Size = new Size(388, 32), ForeColor = Theme.Dim };

            _add = MakeButton("Add", new Point(236, 200), 80, true);
            _add.Click += async (s, e) => await AddAsync();
            var cancel = MakeButton("Cancel", new Point(324, 200), 80, false);
            cancel.DialogResult = DialogResult.Cancel;

            AcceptButton = _add;
            CancelButton = cancel;
            _path.TextChanged += (s, e) => ResetConfirm();

            Controls.AddRange(new Control[] { intro, pathLabel, _path, browse, nameLabel, _name, _status, _add, cancel });
        }

        private TextBox MakeBox(Point at, int width) => new TextBox
        {
            Location = at,
            Width = width,
            BackColor = Field,
            ForeColor = Theme.Title,
            BorderStyle = BorderStyle.FixedSingle,
        };

        private Button MakeButton(string text, Point at, int width, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Location = at,
                Size = new Size(width, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(46, 96, 150) : Color.FromArgb(44, 50, 60),
                ForeColor = Theme.Title,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = primary ? Theme.Cyan : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(56, 116, 180) : Color.FromArgb(58, 66, 80);
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _path.Focus();
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Pick a shared folder under Network, or a mapped drive.",
                ShowNewFolderButton = false,
                RootFolder = Environment.SpecialFolder.MyComputer,
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                    _path.Text = dlg.SelectedPath;
            }
        }

        private void ResetConfirm()
        {
            if (!_confirmUnreachable) return;
            _confirmUnreachable = false;
            _add.Text = "Add";
            _status.Text = "";
        }

        private async Task AddAsync()
        {
            string path = NetworkLocations.Normalize(_path.Text, out string letter);
            if (path == null)
            {
                Status("Enter a share like \\\\server\\share, or a drive letter like Z:", Theme.Orange);
                return;
            }

            if (_confirmUnreachable)
            {
                Finish(letter ?? path);
                return;
            }

            Status("Checking " + path + " …", Theme.Dim);
            _add.Enabled = false;
            try
            {
                Task<(bool ok, long total, long free)> probe = NetworkLocations.ProbeAsync(path);
                Task done = await Task.WhenAny(probe, Task.Delay(6000));
                if (done == probe && probe.Result.ok)
                {
                    Finish(letter ?? path);
                    return;
                }

                _confirmUnreachable = true;
                _add.Text = "Add anyway";
                Status(done == probe
                           ? "Couldn't read that location. It may be offline or need a sign-in. Add it anyway?"
                           : "No answer after 6 seconds. It may be offline. Add it anyway?",
                       Theme.Orange);
            }
            finally
            {
                _add.Enabled = true;
            }
        }

        private void Finish(string path)
        {
            ResultPath = path;
            ResultName = _name.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Status(string text, Color color)
        {
            _status.ForeColor = color;
            _status.Text = text;
        }
    }
}
