using System;
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace DeskMeters.Sources
{
    /// <summary>
    /// Things Windows reports without any driver: RAM, page file, the Task Manager style
    /// CPU clock, and a per-thread load fallback for when LibreHardwareMonitor can't start.
    /// Sampler thread only.
    /// </summary>
    internal sealed class SystemSource : IDisposable
    {
        private PerformanceCounter _perf;      // % Processor Performance (_Total)
        private PerformanceCounter _freq;      // Processor Frequency (_Total), MHz base clock
        private PerformanceCounter _total;
        private PerformanceCounter[] _threads;
        private bool _countersTried;

        private DateTime _nextPageFile = DateTime.MinValue;
        private ulong _pageTotal, _pageUsed;
        private float _baseMHz;

        public string CpuName { get; }
        public string Vendor { get; }

        public SystemSource()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    CpuName = (k?.GetValue("ProcessorNameString") as string)?.Trim();
                    Vendor = k?.GetValue("VendorIdentifier") as string;
                    if (k?.GetValue("~MHz") is int mhz) _baseMHz = mhz;
                }
            }
            catch
            {
                // Name is cosmetic.
            }

            CpuName = CpuName ?? "CPU";
            Vendor = Vendor == null ? "" : Vendor.Contains("AMD") ? "AMD" : Vendor.Contains("Intel") ? "Intel" : "";
        }

        private void EnsureCounters()
        {
            if (_countersTried) return;
            _countersTried = true;

            try
            {
                _perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                _freq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
                _perf.NextValue();
                float f = _freq.NextValue();
                if (f > 0) _baseMHz = f;
            }
            catch (Exception ex)
            {
                Log.Error("Clock counters", ex);
                _perf = null;
            }
        }

        public float ReadClockMHz()
        {
            EnsureCounters();
            if (_perf == null || _baseMHz <= 0) return 0;
            try
            {
                return _baseMHz * _perf.NextValue() / 100f;
            }
            catch
            {
                return 0;
            }
        }

        public void ReadMemory(CpuSnap snap)
        {
            if (Native.TryGetMemory(out Native.MEMORYSTATUSEX m))
            {
                snap.RamTotal = m.ullTotalPhys;
                snap.RamAvail = m.ullAvailPhys;
            }

            // WMI is slow-ish, and page file use changes slowly.
            if (DateTime.UtcNow >= _nextPageFile)
            {
                _nextPageFile = DateTime.UtcNow.AddSeconds(10);
                try
                {
                    ulong total = 0, used = 0;
                    using (var searcher = new ManagementObjectSearcher("SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage"))
                    using (ManagementObjectCollection results = searcher.Get())
                    {
                        foreach (ManagementBaseObject o in results)
                        {
                            using (o)
                            {
                                total += Convert.ToUInt64(o["AllocatedBaseSize"]) * 1024UL * 1024UL;
                                used += Convert.ToUInt64(o["CurrentUsage"]) * 1024UL * 1024UL;
                            }
                        }
                    }
                    _pageTotal = total;
                    _pageUsed = used;
                }
                catch (Exception ex)
                {
                    Log.Error("Page file", ex);
                    _nextPageFile = DateTime.UtcNow.AddMinutes(5);
                }
            }

            snap.PageTotal = _pageTotal;
            snap.PageUsed = _pageUsed;
        }

        /// <summary>Per-thread load from Windows performance counters (no temps).</summary>
        public void ReadLoadsFallback(CpuSnap snap)
        {
            try
            {
                if (_threads == null)
                {
                    int n = Environment.ProcessorCount;
                    _threads = new PerformanceCounter[n];
                    for (int i = 0; i < n; i++)
                    {
                        _threads[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                        _threads[i].NextValue();
                    }
                    _total = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    _total.NextValue();
                }

                int count = _threads.Length;
                snap.ThreadLoad = new float[count];
                snap.ThreadCore = new int[count];
                snap.ThreadTemp = new float?[count];
                for (int i = 0; i < count; i++)
                {
                    snap.ThreadLoad[i] = Fmt.Clamp(_threads[i].NextValue(), 0, 100);
                    snap.ThreadCore[i] = i;
                }
                snap.CoreCount = count;
                snap.Total = Fmt.Clamp(_total.NextValue(), 0, 100);
            }
            catch (Exception ex)
            {
                Log.Error("CPU fallback counters", ex);
                snap.ThreadLoad = new float[0];
                snap.ThreadCore = new int[0];
                snap.ThreadTemp = new float?[0];
            }
        }

        public void Dispose()
        {
            _perf?.Dispose();
            _freq?.Dispose();
            _total?.Dispose();
            if (_threads != null)
                foreach (var c in _threads) c?.Dispose();
        }
    }
}
