using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DeskMeters.Sources
{
    /// <summary>Drive space and activity from DriveInfo and LogicalDisk counters. Sampler thread only.</summary>
    internal sealed class DriveSource : IDisposable
    {
        private sealed class Counters : IDisposable
        {
            public PerformanceCounter Idle, Read, Write;
            public void Dispose() { Idle?.Dispose(); Read?.Dispose(); Write?.Dispose(); }
        }

        private readonly Dictionary<string, Counters> _counters = new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _counterFailed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private List<DriveSnap> _space = new List<DriveSnap>();
        private DateTime _nextScan = DateTime.MinValue;

        /// <summary>Local drives only; network shares come from <see cref="NetworkLocations"/>.</summary>
        public List<DriveSnap> Read()
        {
            if (DateTime.UtcNow >= _nextScan)
            {
                _space = ScanSpace();
                _nextScan = DateTime.UtcNow.AddSeconds(5);
            }

            var result = new List<DriveSnap>(_space.Count);
            for (int i = 0; i < _space.Count; i++)
            {
                DriveSnap src = _space[i];
                var d = new DriveSnap
                {
                    Key = src.Letter, Letter = src.Letter, Path = src.Letter + "\\", Label = src.Label, Ready = src.Ready,
                    Total = src.Total, Free = src.Free,
                };

                if (d.Ready)
                {
                    Counters c = GetCounters(d.Letter);
                    if (c != null)
                    {
                        try
                        {
                            d.Busy = Fmt.Clamp(100f - c.Idle.NextValue(), 0, 100);
                            d.ReadBps = c.Read.NextValue();
                            d.WriteBps = c.Write.NextValue();
                            d.HasRates = true;
                        }
                        catch
                        {
                            DropCounters(d.Letter);
                        }
                    }
                }

                result.Add(d);
            }

            // Forget counters for drives that disappeared.
            foreach (string gone in _counters.Keys.Where(k => _space.All(s => !s.Letter.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
                DropCounters(gone);

            return result;
        }

        private static List<DriveSnap> ScanSpace()
        {
            var list = new List<DriveSnap>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) { Log.Error("Drive scan", ex); return list; }

            foreach (DriveInfo di in drives)
            {
                DriveType type;
                try { type = di.DriveType; }
                catch { continue; }

                // Network drives are handled separately: IsReady on a dead share can hang for ages.
                if (type != DriveType.Fixed && type != DriveType.Removable && type != DriveType.CDRom)
                    continue;

                var d = new DriveSnap { Letter = di.Name.TrimEnd('\\') };
                try
                {
                    d.Ready = di.IsReady;
                    if (d.Ready)
                    {
                        d.Label = di.VolumeLabel;
                        d.Total = di.TotalSize;
                        d.Free = di.TotalFreeSpace;
                    }
                }
                catch
                {
                    d.Ready = false;
                }

                // Skip empty card readers etc.; keep empty optical drives, like the original gadget did.
                if (!d.Ready && type == DriveType.Removable)
                    continue;

                list.Add(d);
            }
            return list;
        }

        private Counters GetCounters(string letter)
        {
            if (_counters.TryGetValue(letter, out Counters c)) return c;
            if (_counterFailed.TryGetValue(letter, out DateTime retry) && DateTime.UtcNow < retry) return null;

            try
            {
                c = new Counters
                {
                    Idle = new PerformanceCounter("LogicalDisk", "% Idle Time", letter, true),
                    Read = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", letter, true),
                    Write = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", letter, true),
                };
                c.Idle.NextValue();
                c.Read.NextValue();
                c.Write.NextValue();
                _counters[letter] = c;
                return c;
            }
            catch (Exception ex)
            {
                Log.Info($"No disk counters for {letter}: {ex.Message}");
                c?.Dispose();
                _counterFailed[letter] = DateTime.UtcNow.AddMinutes(1);
                return null;
            }
        }

        private void DropCounters(string letter)
        {
            if (_counters.TryGetValue(letter, out Counters c))
            {
                c.Dispose();
                _counters.Remove(letter);
            }
        }

        public void Dispose()
        {
            foreach (Counters c in _counters.Values) c.Dispose();
            _counters.Clear();
        }
    }
}
