using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using DeskMeters.Sources;

namespace DeskMeters
{
    /// <summary>
    /// Owns every data source and polls them on one background thread, so a slow sensor
    /// never makes a meter stutter while you drag it. Each tick produces a fresh
    /// <see cref="Snapshot"/> that is handed to the UI.
    /// </summary>
    internal sealed class Sampler : IDisposable
    {
        private readonly Action<Snapshot> _publish;
        private readonly Settings _settings;
        private readonly Thread _thread;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public NetworkSource Network { get; private set; }
        public NetServices NetServices { get; private set; }
        public NetworkLocations Locations { get; private set; }

        // Written by the UI thread, read by the sampler.
        public volatile int IntervalMs = 1000;
        public volatile string PreferredAdapterId;
        public volatile string PreferredGpu;
        public volatile bool IncludeNetworkDrives;
        public volatile bool WantCpu = true, WantGpu = true, WantNet = true, WantDrives = true;

        public static bool IsAdmin { get; } = CheckAdmin();

        public Sampler(Settings settings, Action<Snapshot> publish)
        {
            _settings = settings;
            _publish = publish;
            _thread = new Thread(Loop) { IsBackground = true, Name = "DeskMeters sampler", Priority = ThreadPriority.BelowNormal };
        }

        public void Start() => _thread.Start();

        /// <summary>Take the next sample now (after a setting change).</summary>
        public void Poke() => _wake.Set();

        private static bool CheckAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void Loop()
        {
            var system = new SystemSource();
            var drives = new DriveSource();
            Network = new NetworkSource(_settings);
            NetServices = new NetServices();
            Locations = new NetworkLocations(_settings) { IncludeMapped = IncludeNetworkDrives };

            // Show something immediately; LibreHardwareMonitor can take a few seconds to open.
            Publish(new Snapshot { Time = DateTime.Now, IsAdmin = IsAdmin, SensorsReady = false });

            var hardware = new HardwareSource();
            hardware.Open();

            var sw = new Stopwatch();
            try
            {
                while (true)
                {
                    sw.Restart();
                    try
                    {
                        Publish(Sample(hardware, system, drives));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Sample", ex);
                    }

                    int wait = Math.Max(100, IntervalMs - (int)sw.ElapsedMilliseconds);
                    int signalled = WaitHandle.WaitAny(new WaitHandle[] { _stop, _wake }, wait);
                    if (signalled == 0) break;
                }
            }
            finally
            {
                Network.PersistTotals();
                NetServices.Dispose();
                Locations.Dispose();
                hardware.Dispose();
                system.Dispose();
                drives.Dispose();
            }
        }

        private Snapshot Sample(HardwareSource hw, SystemSource system, DriveSource drives)
        {
            var snap = new Snapshot
            {
                Time = DateTime.Now,
                IsAdmin = IsAdmin,
                PawnIoInstalled = hw.PawnIoInstalled,
                SensorsReady = true,
                SensorError = hw.Error,
            };

            if (WantCpu)
            {
                var cpu = new CpuSnap { Name = system.CpuName, Vendor = system.Vendor };
                cpu.ClockMHz = system.ReadClockMHz();
                if (!hw.ReadCpu(cpu))
                    system.ReadLoadsFallback(cpu);
                cpu.Name = system.CpuName ?? cpu.Name;
                system.ReadMemory(cpu);
                snap.Cpu = cpu;
            }

            if (WantGpu)
                snap.Gpu = hw.ReadGpu(PreferredGpu);

            if (WantNet)
            {
                NetSnap net = Network.Read(PreferredAdapterId);
                NetServices.CurrentIntIp = net.IntIp;
                net.ExtIp = NetServices.ExtIp;
                net.Blacklist = NetServices.Blacklist;
                net.BlacklistListed = NetServices.BlacklistListed;
                net.FirewallOn = NetServices.FirewallOn;
                net.FirewallProfile = NetServices.FirewallProfile;
                snap.Net = net;
            }

            if (WantDrives)
            {
                List<DriveSnap> list = drives.Read();
                if (Locations.IncludeMapped != IncludeNetworkDrives)
                {
                    Locations.IncludeMapped = IncludeNetworkDrives;
                    Locations.Poke();
                }
                list.AddRange(Locations.Read());
                snap.Drives = list.ToArray();
            }

            return snap;
        }

        private void Publish(Snapshot s)
        {
            try { _publish(s); }
            catch (Exception ex) { Log.Error("Publish", ex); }
        }

        public void Dispose()
        {
            _stop.Set();
            if (_thread.IsAlive && !_thread.Join(4000))
            {
                // Leave the handles alone; the background thread dies with the process.
                Log.Info("Sampler did not stop in time");
                return;
            }
            _stop.Dispose();
            _wake.Dispose();
        }
    }
}
