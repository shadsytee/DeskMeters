using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeskMeters
{
    /// <summary>
    /// Flat key=value settings stored in %APPDATA%\DeskMeters\settings.ini.
    /// UI thread only, except <see cref="Save"/> which takes a snapshot under a lock.
    /// </summary>
    internal sealed class Settings
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();
        private bool _dirty;

        public string FilePath { get; }

        public Settings()
        {
            FilePath = Path.Combine(Log.Folder, "settings.ini");
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    _values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Settings.Load", ex);
            }
        }

        public void Save(bool force = false)
        {
            List<KeyValuePair<string, string>> copy;
            lock (_gate)
            {
                if (!_dirty && !force)
                    return;
                copy = new List<KeyValuePair<string, string>>(_values);
                _dirty = false;
            }

            try
            {
                copy.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                var sb = new StringBuilder();
                sb.AppendLine("# DeskMeters settings. Safe to delete to reset everything.");
                foreach (var kv in copy)
                    sb.Append(kv.Key).Append('=').AppendLine(kv.Value);

                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath))
                    File.Replace(tmp, FilePath, null);
                else
                    File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Error("Settings.Save", ex);
            }
        }

        public string Get(string key, string fallback = null)
        {
            lock (_gate)
                return _values.TryGetValue(key, out string v) ? v : fallback;
        }

        public void Set(string key, string value)
        {
            lock (_gate)
            {
                if (value == null)
                {
                    if (_values.Remove(key)) _dirty = true;
                    return;
                }
                if (_values.TryGetValue(key, out string old) && old == value)
                    return;
                _values[key] = value;
                _dirty = true;
            }
        }

        public int GetInt(string key, int fallback) =>
            int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

        public double GetDouble(string key, double fallback) =>
            double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        public void SetDouble(string key, double value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));

        public bool GetBool(string key, bool fallback)
        {
            string v = Get(key);
            if (v == null) return fallback;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

        public bool Has(string key)
        {
            lock (_gate)
                return _values.ContainsKey(key);
        }
    }
}
