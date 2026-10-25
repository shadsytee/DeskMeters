using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeskMeters.Sources
{
    /// <summary>Adapter throughput, IPs and persisted totals. Sampler thread only.</summary>
    internal sealed class NetworkSource
    {
        private readonly Settings _settings;
        private NetworkInterface[] _adapters = new NetworkInterface[0];
        private DateTime _nextScan = DateTime.MinValue;

        private string _currentId;
        private long _lastSent = -1, _lastRecv = -1;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTime;
        private double _totalUp, _totalDown;
        private DateTime _nextPersist = DateTime.MinValue;

        public volatile bool ResetTotalsRequested;

        public NetworkSource(Settings settings) { _settings = settings; }

        private void Scan()
        {
            try
            {
                _adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                n.OperationalStatus == OperationalStatus.Up)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Log.Error("Adapter scan", ex);
            }
        }

        private static bool HasGateway(NetworkInterface n)
        {
            try
            {
                return n.GetIPProperties().GatewayAddresses.Any(g => g.Address != null &&
                                                                     !g.Address.Equals(IPAddress.Any) &&
                                                                     !g.Address.Equals(IPAddress.IPv6Any));
            }
            catch
            {
                return false;
            }
        }

        private static int Score(NetworkInterface n)
        {
            int score = 0;
            if (HasGateway(n)) score += 100;
            string d = (n.Description + " " + n.Name).ToLowerInvariant();
            bool virt = d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("vpn") ||
                        d.Contains("tap-") || d.Contains("wireguard") || d.Contains("tailscale") || d.Contains("zerotier") ||
                        d.Contains("loopback") || d.Contains("bluetooth");
            if (!virt) score += 50;
            if (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) score += 20;
            if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 10;
            return score;
        }

        public NetSnap Read(string preferredId)
        {
            if (DateTime.UtcNow >= _nextScan)
            {
                Scan();
                _nextScan = DateTime.UtcNow.AddSeconds(10);
            }

            var snap = new NetSnap
            {
                Adapters = _adapters.Select(a => new KeyValuePair<string, string>(a.Id, a.Description)).ToList()
            };

            NetworkInterface ni = null;
            if (!string.IsNullOrEmpty(preferredId))
                ni = _adapters.FirstOrDefault(a => a.Id == preferredId);
            if (ni == null)
                ni = _adapters.OrderByDescending(Score).ThenByDescending(a => a.Speed).FirstOrDefault();

            if (ni == null)
            {
                snap.AdapterName = "No active network adapter";
                return snap;
            }

            if (ni.Id != _currentId)
            {
                PersistTotals();
                _currentId = ni.Id;
                _lastSent = _lastRecv = -1;
                _totalUp = _settings.GetDouble("net.total." + _currentId + ".up", 0);
                _totalDown = _settings.GetDouble("net.total." + _currentId + ".down", 0);
            }

            if (ResetTotalsRequested)
            {
                ResetTotalsRequested = false;
                _totalUp = _totalDown = 0;
                _nextPersist = DateTime.MinValue;
            }

            snap.AdapterId = ni.Id;
            snap.AdapterName = ni.Description;
            snap.LinkSpeed = SafeSpeed(ni);

            try
            {
                IPInterfaceProperties props = ni.GetIPProperties();
                UnicastIPAddressInformation v4 = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .OrderBy(u => u.Address.ToString().StartsWith("169.254.") ? 1 : 0)
                    .FirstOrDefault();
                snap.IntIp = v4?.Address.ToString();
            }
            catch
            {
                snap.IntIp = null;
            }

            try
            {
                IPInterfaceStatistics stats = ni.GetIPStatistics();
                long sent = stats.BytesSent, recv = stats.BytesReceived;
                double now = _clock.Elapsed.TotalSeconds;

                if (_lastSent >= 0)
                {
                    double dt = Math.Max(0.05, now - _lastTime);
                    long dUp = sent - _lastSent, dDown = recv - _lastRecv;
                    if (dUp < 0) dUp = 0;      // counter reset (adapter bounced)
                    if (dDown < 0) dDown = 0;
                    snap.UpBps = dUp / dt;
                    snap.DownBps = dDown / dt;
                    _totalUp += dUp;
                    _totalDown += dDown;
                }

                _lastSent = sent;
                _lastRecv = recv;
                _lastTime = now;
                snap.SessionUp = sent;
                snap.SessionDown = recv;
            }
            catch (Exception ex)
            {
                Log.Error("Adapter stats", ex);
                _nextScan = DateTime.MinValue; // adapter probably went away
            }

            snap.TotalUp = _totalUp;
            snap.TotalDown = _totalDown;

            if (DateTime.UtcNow >= _nextPersist)
            {
                PersistTotals();
                _nextPersist = DateTime.UtcNow.AddSeconds(30);
            }

            return snap;
        }

        private static long SafeSpeed(NetworkInterface ni)
        {
            try { return ni.Speed; }
            catch { return 0; }
        }

        public void PersistTotals()
        {
            if (string.IsNullOrEmpty(_currentId)) return;
            _settings.SetDouble("net.total." + _currentId + ".up", Math.Round(_totalUp));
            _settings.SetDouble("net.total." + _currentId + ".down", Math.Round(_totalDown));
        }
    }
}
