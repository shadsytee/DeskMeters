using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace DeskMeters.Sources
{
    /// <summary>
    /// CPU and GPU sensors through LibreHardwareMonitor. Sampler thread only.
    /// Works without admin (loads, GPU sensors via the vendor APIs); CPU temperatures,
    /// clocks and power need admin plus the PawnIO driver.
    /// </summary>
    internal sealed class HardwareSource : IDisposable
    {
        private static readonly Regex ThreadName = new Regex(@"^CPU Core #(\d+)(?: Thread #(\d+))?$", RegexOptions.Compiled);
        private static readonly Regex CcdName = new Regex(@"^CCD ?(\d+)", RegexOptions.Compiled);

        private Computer _computer;
        private IHardware _cpu;
        private readonly List<IHardware> _gpus = new List<IHardware>();

        public bool IsOpen { get; private set; }
        public string Error { get; private set; }
        public bool PawnIoInstalled { get; private set; }

        public void Open()
        {
            try
            {
                try { PawnIoInstalled = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
                catch { PawnIoInstalled = false; }

                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                };
                _computer.Open();

                foreach (IHardware hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu && _cpu == null)
                        _cpu = hw;
                    else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel)
                        _gpus.Add(hw);
                }

                // Discrete cards first so "first GPU" is the one people mean.
                _gpus.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
                IsOpen = true;
                Log.Info($"Sensors open: CPU={_cpu?.Name ?? "none"}, GPUs={string.Join(", ", _gpus.Select(g => g.Name))}, PawnIO={PawnIoInstalled}");
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Log.Error("HardwareSource.Open", ex);
            }
        }

        private static int Rank(IHardware hw) => hw.HardwareType == HardwareType.GpuIntel ? 1 : 0;

        public string[] GpuNames => _gpus.Select(g => g.Name).ToArray();

        public bool HasCpu => _cpu != null;

        /// <summary>Fills loads, temperatures and power. Returns false if no LHM CPU is available.</summary>
        public bool ReadCpu(CpuSnap snap)
        {
            if (_cpu == null)
                return false;

            try
            {
                _cpu.Update();
            }
            catch (Exception ex)
            {
                Log.Error("CPU update", ex);
                return false;
            }

            ISensor[] sensors = _cpu.Sensors;
            snap.Name = _cpu.Name;

            // Per-thread loads. Sensor.Index is (logical processor + 2) for thread sensors.
            var threads = new List<(int logical, int core, float load)>();
            foreach (ISensor s in sensors)
            {
                if (s.SensorType != SensorType.Load) continue;
                if (s.Name == "CPU Total") { snap.Total = s.Value ?? 0; continue; }

                Match m = ThreadName.Match(s.Name);
                if (m.Success)
                    threads.Add((s.Index - 2, int.Parse(m.Groups[1].Value) - 1, s.Value ?? 0));
            }

            if (threads.Count == 0)
                return false;

            threads.Sort((a, b) => a.logical.CompareTo(b.logical));
            int n = threads.Count;
            snap.ThreadLoad = new float[n];
            snap.ThreadCore = new int[n];
            snap.ThreadTemp = new float?[n];
            for (int i = 0; i < n; i++)
            {
                snap.ThreadLoad[i] = threads[i].load;
                snap.ThreadCore[i] = threads[i].core;
            }
            snap.CoreCount = threads.Max(t => t.core) + 1;

            // Temperatures.
            var named = new Dictionary<string, float>();
            var ccds = new SortedDictionary<int, float>();
            var coreTemps = new List<(int index, float value)>();

            foreach (ISensor s in sensors)
            {
                if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
                string name = s.Name;
                float v = s.Value.Value;
                if (v <= 0 || v > 150) continue;

                if (name == "Core (Tctl/Tdie)" || name == "CPU Package" || name == "Core (Tdie)" || name == "Core (Tctl)")
                {
                    named[name] = v;
                    continue;
                }

                Match ccd = CcdName.Match(name);
                if (ccd.Success && !name.Contains("Max") && !name.Contains("Average"))
                {
                    ccds[int.Parse(ccd.Groups[1].Value)] = v;
                    continue;
                }

                // Intel per-core sensors: "CPU Core #1", "P-Core #1", "E-Core #3"…
                if (name.Contains("Core #") && !name.Contains("Distance") && !name.Contains("Max") && !name.Contains("Average"))
                    coreTemps.Add((s.Index, v));
            }

            float? package = null;
            foreach (string key in new[] { "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)" })
            {
                if (named.TryGetValue(key, out float pv)) { package = pv; break; }
            }

            coreTemps.Sort((a, b) => a.index.CompareTo(b.index));
            float[] ccdArray = ccds.Values.ToArray();

            for (int i = 0; i < n; i++)
            {
                int core = snap.ThreadCore[i];
                if (coreTemps.Count == snap.CoreCount)
                    snap.ThreadTemp[i] = coreTemps[core].value;
                else if (ccdArray.Length > 0)
                    snap.ThreadTemp[i] = ccdArray[Math.Min(ccdArray.Length - 1, core * ccdArray.Length / Math.Max(1, snap.CoreCount))];
                else
                    snap.ThreadTemp[i] = package;
            }
            snap.PackageTemp = package ?? (ccdArray.Length > 0 ? ccdArray.Max() : (float?)null);

            // Power and clock.
            foreach (ISensor s in sensors)
            {
                if (s.SensorType == SensorType.Power && (s.Name == "Package" || s.Name == "CPU Package") && s.Value.HasValue)
                    snap.PackagePower = s.Value;
            }

            if (snap.ClockMHz <= 0)
            {
                var clocks = sensors.Where(s => s.SensorType == SensorType.Clock && s.Value > 0 &&
                                                (s.Name.StartsWith("Core #", StringComparison.Ordinal) || s.Name.Contains("Core #")) &&
                                                !s.Name.Contains("Effective")).Select(s => s.Value.Value).ToList();
                if (clocks.Count > 0)
                    snap.ClockMHz = clocks.Average();
            }

            return true;
        }

        public GpuSnap ReadGpu(string preferredName)
        {
            var snap = new GpuSnap { AllNames = GpuNames, SelectedIndex = -1 };
            if (_gpus.Count == 0)
                return snap;

            int idx = 0;
            if (!string.IsNullOrEmpty(preferredName))
            {
                int found = _gpus.FindIndex(g => g.Name == preferredName);
                if (found >= 0) idx = found;
            }

            IHardware gpu = _gpus[idx];
            snap.SelectedIndex = idx;
            snap.Name = gpu.Name;

            try
            {
                gpu.Update();
            }
            catch (Exception ex)
            {
                Log.Error("GPU update", ex);
                return snap;
            }

            ISensor[] s = gpu.Sensors;

            snap.Load = Find(s, SensorType.Load, "GPU Core") ?? Find(s, SensorType.Load, "D3D 3D") ?? MaxStartsWith(s, SensorType.Load, "D3D ");
            snap.MemCtrlLoad = Find(s, SensorType.Load, "GPU Memory Controller");
            snap.VideoLoad = Find(s, SensorType.Load, "GPU Video Engine") ?? Find(s, SensorType.Load, "D3D Video Decode");
            snap.Temp = Find(s, SensorType.Temperature, "GPU Core");
            snap.HotSpot = Find(s, SensorType.Temperature, "GPU Hot Spot");
            snap.CoreClock = Find(s, SensorType.Clock, "GPU Core");
            snap.MemClock = Find(s, SensorType.Clock, "GPU Memory");
            snap.FanRpm = First(s, SensorType.Fan);
            snap.FanPct = First(s, SensorType.Control);
            snap.Power = Find(s, SensorType.Power, "GPU Package") ?? Find(s, SensorType.Power, "GPU PPT") ??
                         Find(s, SensorType.Power, "GPU Power") ?? Find(s, SensorType.Power, "GPU Total") ?? Find(s, SensorType.Power, "GPU Core");

            snap.MemUsedMB = Find(s, SensorType.SmallData, "GPU Memory Used") ?? Find(s, SensorType.SmallData, "D3D Dedicated Memory Used");
            snap.MemTotalMB = Find(s, SensorType.SmallData, "GPU Memory Total") ?? Find(s, SensorType.SmallData, "D3D Dedicated Memory Total");

            if (gpu.HardwareType == HardwareType.GpuIntel && (snap.MemTotalMB == null || snap.MemTotalMB <= 0))
            {
                snap.MemUsedMB = Find(s, SensorType.SmallData, "D3D Shared Memory Used");
                snap.MemTotalMB = Find(s, SensorType.SmallData, "D3D Shared Memory Total");
            }

            if (snap.MemTotalMB.HasValue && snap.MemTotalMB <= 0) snap.MemTotalMB = null;
            return snap;
        }

        private static float? Find(ISensor[] sensors, SensorType type, string name)
        {
            foreach (ISensor s in sensors)
                if (s.SensorType == type && s.Name == name && s.Value.HasValue && !float.IsNaN(s.Value.Value))
                    return s.Value;
            return null;
        }

        private static float? First(ISensor[] sensors, SensorType type)
        {
            foreach (ISensor s in sensors)
                if (s.SensorType == type && s.Value.HasValue && !float.IsNaN(s.Value.Value))
                    return s.Value;
            return null;
        }

        private static float? MaxStartsWith(ISensor[] sensors, SensorType type, string prefix)
        {
            float? best = null;
            foreach (ISensor s in sensors)
                if (s.SensorType == type && s.Name.StartsWith(prefix, StringComparison.Ordinal) && s.Value.HasValue && (best == null || s.Value > best))
                    best = s.Value;
            return best;
        }

        public void Dispose()
        {
            try { _computer?.Close(); }
            catch (Exception ex) { Log.Error("HardwareSource.Close", ex); }
            _computer = null;
        }
    }
}
