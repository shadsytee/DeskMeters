using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DeskMeters.Sources
{
    /// <summary>One configured network location: a UNC share, optionally reached through a drive letter.</summary>
    internal sealed class NetLocation
    {
        public string Path;      // \\server\share, or "Z:" if it couldn't be resolved
        public string Name;      // user's label (may be empty)
        public string Letter;    // "Z:" when this came from a mapped drive
        public bool Custom;      // added by the user (vs. found as a mapped drive)

        public string Key => (Path ?? "").TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>
    /// Space and activity for network shares and mapped drives.
    ///
    /// Querying a share can hang for 20+ seconds when the server is asleep or gone, so the
    /// space checks run on their own thread with a timeout and never block the sampler.
    /// Mapped drives are read from HKCU\Network as well as DriveInfo, because an elevated
    /// process (which DeskMeters is when it shows temperatures) can't see drive letters
    /// that were mapped in the normal, non-elevated session.
    /// </summary>
    internal sealed class NetworkLocations : IDisposable
    {
        private sealed class State
        {
            public long Total, Free;
            public bool Ok;
            public bool Checked;
            public Task Pending;
        }

        private static readonly Regex UncRoot = new Regex(@"^\\\\[^\\/]+\\[^\\/]+", RegexOptions.Compiled);
        private static readonly Regex DriveOnly = new Regex(@"^[A-Za-z]:\\?$", RegexOptions.Compiled);

        private readonly Settings _settings;
        private readonly ConcurrentDictionary<string, State> _state = new ConcurrentDictionary<string, State>();
        private readonly Thread _thread;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile List<NetLocation> _targets = new List<NetLocation>();

        // SMB client counters (sampler thread only)
        private readonly Dictionary<string, (PerformanceCounter read, PerformanceCounter write)> _smb =
            new Dictionary<string, (PerformanceCounter, PerformanceCounter)>(StringComparer.OrdinalIgnoreCase);
        private string[] _smbInstances = new string[0];
        private DateTime _nextSmbScan = DateTime.MinValue;

        public volatile bool IncludeMapped;

        public NetworkLocations(Settings settings)
        {
            _settings = settings;
            _thread = new Thread(Loop) { IsBackground = true, Name = "DeskMeters network locations", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        /// <summary>Recheck now (after adding or removing a location).</summary>
        public void Poke() => _wake.Set();

        // ---- configuration (shared with the UI) -------------------------------------------

        public static List<NetLocation> LoadCustom(Settings settings)
        {
            var list = new List<NetLocation>();
            string raw = settings.Get("drives.locations", "");
            foreach (string entry in raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split('>');
                string path = parts[0].Trim();
                if (path.Length == 0) continue;
                list.Add(new NetLocation { Path = path, Name = parts.Length > 1 ? parts[1].Trim() : "", Custom = true });
            }
            return list;
        }

        public static void SaveCustom(Settings settings, IEnumerable<NetLocation> list)
        {
            // '|' and '>' can't appear in Windows paths; names are scrubbed of them.
            settings.Set("drives.locations", string.Join("|", list.Select(l =>
                l.Path + (string.IsNullOrEmpty(l.Name) ? "" : ">" + l.Name.Replace("|", " ").Replace(">", " ")))));
        }

        /// <summary>
        /// Clean up what the user typed: forward slashes, trailing backslashes, a bare drive
        /// letter resolved to its UNC path when possible. Returns null if it isn't usable.
        /// </summary>
        public static string Normalize(string input, out string letter)
        {
            letter = null;
            if (string.IsNullOrWhiteSpace(input)) return null;
            string s = input.Trim().Trim('"').Replace('/', '\\');

            if (DriveOnly.IsMatch(s))
            {
                letter = char.ToUpperInvariant(s[0]) + ":";
                return ResolveLetter(letter) ?? letter;
            }

            s = s.TrimEnd('\\');
            return UncRoot.IsMatch(s) ? s : null;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

        /// <summary>"Z:" → "\\nas\media", from the live mapping or the persistent one in HKCU.</summary>
        public static string ResolveLetter(string letter)
        {
            try
            {
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                int rc = WNetGetConnection(letter, sb, ref len);
                if ((rc == 0 || rc == 1201) && sb.Length > 0) // 1201 = remembered but not connected
                    return sb.ToString().TrimEnd('\\');
            }
            catch
            {
                // fall through to the registry
            }

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Network\" + letter.TrimEnd(':')))
                {
                    string remote = k?.GetValue("RemotePath") as string;
                    if (!string.IsNullOrEmpty(remote)) return remote.TrimEnd('\\');
                }
            }
            catch
            {
                // not mapped
            }
            return null;
        }

        /// <summary>Mapped drive letters visible to this process or remembered for this user.</summary>
        public static List<NetLocation> MappedDrives()
        {
            var byLetter = new SortedDictionary<string, NetLocation>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (RegistryKey net = Registry.CurrentUser.OpenSubKey("Network"))
                {
                    if (net != null)
                    {
                        foreach (string sub in net.GetSubKeyNames())
                        {
                            if (sub.Length != 1) continue;
                            using (RegistryKey k = net.OpenSubKey(sub))
                            {
                                string remote = (k?.GetValue("RemotePath") as string)?.TrimEnd('\\');
                                if (string.IsNullOrEmpty(remote)) continue;
                                string letter = sub.ToUpperInvariant() + ":";
                                byLetter[letter] = new NetLocation { Path = remote, Letter = letter, Name = "" };
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mapped drives (registry)", ex);
            }

            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    // DriveType is cheap and never touches the network; IsReady can hang, so it isn't used.
                    if (di.DriveType != DriveType.Network) continue;
                    string letter = di.Name.Substring(0, 2).ToUpperInvariant();
                    if (byLetter.ContainsKey(letter)) continue;
                    string remote = ResolveLetter(letter);
                    byLetter[letter] = new NetLocation { Path = remote ?? letter, Letter = letter, Name = "" };
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mapped drives (DriveInfo)", ex);
            }

            return byLetter.Values.ToList();
        }

        /// <summary>Custom locations first, then mapped drives that aren't already listed.</summary>
        private List<NetLocation> BuildTargets()
        {
            List<NetLocation> list = LoadCustom(_settings);
            foreach (NetLocation c in list)
            {
                if (DriveOnly.IsMatch(c.Path))
                {
                    c.Letter = c.Path.Substring(0, 2).ToUpperInvariant();
                    c.Path = ResolveLetter(c.Letter) ?? c.Letter;
                }
            }

            if (IncludeMapped)
            {
                foreach (NetLocation m in MappedDrives())
                {
                    NetLocation existing = list.FirstOrDefault(l => l.Key == m.Key);
                    if (existing != null)
                    {
                        if (existing.Letter == null) existing.Letter = m.Letter;
                        continue;
                    }
                    list.Add(m);
                }
            }
            return list;
        }

        // ---- polling ------------------------------------------------------------------------

        private void Loop()
        {
            while (true)
            {
                try
                {
                    List<NetLocation> targets = BuildTargets();
                    _targets = targets;

                    foreach (NetLocation t in targets)
                    {
                        State st = _state.GetOrAdd(t.Key, _ => new State());
                        if (st.Pending != null && !st.Pending.IsCompleted)
                            continue; // still hung on a previous attempt; don't pile up

                        string path = t.Path.EndsWith("\\") ? t.Path : t.Path + "\\";
                        st.Pending = Task.Run(() =>
                        {
                            bool ok = GetDiskFreeSpaceEx(path, out ulong free, out ulong total, out _);
                            st.Ok = ok && total > 0;
                            if (st.Ok)
                            {
                                st.Total = (long)total;
                                st.Free = (long)free;
                            }
                            st.Checked = true;
                        });
                    }

                    // Give quick answers a moment so a new location shows up promptly.
                    Task[] pending = targets.Select(t => _state.TryGetValue(t.Key, out State s) ? s.Pending : null)
                                            .Where(p => p != null).ToArray();
                    if (pending.Length > 0) Task.WaitAll(pending, 5000);

                    // Anything still running after the wait counts as unreachable for now.
                    foreach (NetLocation t in targets)
                        if (_state.TryGetValue(t.Key, out State s) && s.Pending != null && !s.Pending.IsCompleted)
                        {
                            s.Ok = false;
                            s.Checked = true;
                        }
                }
                catch (Exception ex)
                {
                    Log.Error("Network locations", ex);
                }

                if (WaitHandle.WaitAny(new WaitHandle[] { _stop, _wake }, 15000) == 0) return;
            }
        }

        /// <summary>Current view for the Drives meter (sampler thread).</summary>
        public List<DriveSnap> Read()
        {
            List<NetLocation> targets = _targets;
            var list = new List<DriveSnap>(targets.Count);
            if (targets.Count > 0) RefreshSmbInstances();

            foreach (NetLocation t in targets)
            {
                _state.TryGetValue(t.Key, out State st);
                var d = new DriveSnap
                {
                    Key = t.Key,
                    Letter = t.Letter,
                    Path = t.Path,
                    Label = string.IsNullOrEmpty(t.Name) ? FriendlyName(t.Path) : t.Name,
                    Network = true,
                    Custom = t.Custom,
                    Checking = st == null || !st.Checked,
                    Ready = st != null && st.Ok,
                    Total = st?.Total ?? 0,
                    Free = st?.Free ?? 0,
                };

                if (d.Ready && TryRates(t.Path, out double r, out double w))
                {
                    d.HasRates = true;
                    d.ReadBps = r;
                    d.WriteBps = w;
                }
                list.Add(d);
            }
            return list;
        }

        /// <summary>"\\nas\media" → "media on nas".</summary>
        public static string FriendlyName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            Match m = Regex.Match(path, @"^\\\\([^\\]+)\\([^\\]+)");
            return m.Success ? m.Groups[2].Value + " on " + m.Groups[1].Value : path;
        }

        private void RefreshSmbInstances()
        {
            if (DateTime.UtcNow < _nextSmbScan) return;
            _nextSmbScan = DateTime.UtcNow.AddSeconds(30);
            try
            {
                _smbInstances = PerformanceCounterCategory.Exists("SMB Client Shares")
                    ? new PerformanceCounterCategory("SMB Client Shares").GetInstanceNames()
                    : new string[0];
            }
            catch
            {
                _smbInstances = new string[0];
            }
        }

        private bool TryRates(string unc, out double read, out double write)
        {
            read = write = 0;
            string instance = _smbInstances.FirstOrDefault(i => string.Equals(i.TrimEnd('\\'), unc, StringComparison.OrdinalIgnoreCase));
            if (instance == null) return false;

            try
            {
                if (!_smb.TryGetValue(instance, out var c))
                {
                    c = (new PerformanceCounter("SMB Client Shares", "Read Bytes/sec", instance, true),
                         new PerformanceCounter("SMB Client Shares", "Write Bytes/sec", instance, true));
                    c.read.NextValue();
                    c.write.NextValue();
                    _smb[instance] = c;
                }
                read = c.read.NextValue();
                write = c.write.NextValue();
                return true;
            }
            catch
            {
                if (_smb.TryGetValue(instance, out var bad))
                {
                    bad.read.Dispose();
                    bad.write.Dispose();
                    _smb.Remove(instance);
                }
                return false;
            }
        }

        /// <summary>One-off reachability check for the Add dialog.</summary>
        public static Task<(bool ok, long total, long free)> ProbeAsync(string path)
        {
            string p = path.EndsWith("\\") ? path : path + "\\";
            return Task.Run(() =>
            {
                bool ok = GetDiskFreeSpaceEx(p, out ulong free, out ulong total, out _);
                return (ok && total > 0, (long)total, (long)free);
            });
        }

        public void Dispose()
        {
            _stop.Set();
            if (_thread.Join(2000))
            {
                _stop.Dispose();
                _wake.Dispose();
            }
            foreach (var c in _smb.Values)
            {
                c.read.Dispose();
                c.write.Dispose();
            }
        }
    }
}
