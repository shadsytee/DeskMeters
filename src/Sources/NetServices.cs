using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DeskMeters.Sources
{
    /// <summary>
    /// Slow network lookups that must never stall the sampler: external IP, DNS blacklist
    /// check and Windows Firewall state. Runs on the thread pool; results are read lock-free.
    /// </summary>
    internal sealed class NetServices : IDisposable
    {
        private static readonly string[] IpEndpoints =
        {
            "https://api.ipify.org",
            "https://checkip.amazonaws.com",
            "https://icanhazip.com",
        };

        // Public DNS blacklists that answer anonymous queries. A listing answers 127.0.0.x;
        // 127.255.255.x means the list refused the query (for example Spamhaus via a public resolver).
        private static readonly string[] Dnsbls =
        {
            "zen.spamhaus.org",
            "bl.spamcop.net",
            "b.barracudacentral.org",
            "psbl.surriel.com",
            "dnsbl-1.uceprotect.net",
            "bl.mailspike.net",
            "dnsbl.dronebl.org",
        };

        private readonly Timer _timer;
        private int _busy;
        private DateTime _nextIp = DateTime.MinValue;
        private DateTime _nextBlacklist = DateTime.MaxValue;
        private DateTime _nextFirewall = DateTime.MinValue;
        private string _lastIntIp;

        private volatile string _extIp;
        private volatile string _blacklist;
        private volatile int _blacklistListed = -1;
        private volatile object _firewallOn;   // boxed bool or null
        private volatile string _firewallProfile;

        public volatile string CurrentIntIp;
        public volatile bool RefreshRequested;

        public string ExtIp => _extIp;
        public string Blacklist => _blacklist;
        public int BlacklistListed => _blacklistListed;
        public bool? FirewallOn => _firewallOn as bool?;
        public string FirewallProfile => _firewallProfile;

        public NetServices()
        {
            _timer = new Timer(_ => Tick(), null, 1500, 5000);
        }

        private void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                DateTime now = DateTime.UtcNow;
                string intIp = CurrentIntIp;
                bool force = RefreshRequested;
                RefreshRequested = false;

                if (now >= _nextFirewall || force)
                {
                    ReadFirewall();
                    _nextFirewall = now.AddSeconds(30);
                }

                if (now >= _nextIp || force || intIp != _lastIntIp)
                {
                    _lastIntIp = intIp;
                    string ip = FetchExternalIp();
                    if (ip != null)
                    {
                        bool changed = ip != _extIp;
                        _extIp = ip;
                        _nextIp = now.AddMinutes(15);
                        if (changed || force) _nextBlacklist = now;
                    }
                    else
                    {
                        _nextIp = now.AddMinutes(1);
                    }
                }

                if (_extIp != null && (now >= _nextBlacklist || force))
                {
                    CheckBlacklists(_extIp);
                    _nextBlacklist = now.AddHours(6);
                }
            }
            catch (Exception ex)
            {
                Log.Error("NetServices", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static string FetchExternalIp()
        {
            foreach (string url in IpEndpoints)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 5000;
                    req.ReadWriteTimeout = 5000;
                    req.UserAgent = "DeskMeters/1.0";
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream()))
                    {
                        string text = reader.ReadToEnd().Trim();
                        if (IPAddress.TryParse(text, out IPAddress addr))
                            return addr.ToString();
                    }
                }
                catch
                {
                    // try the next one
                }
            }
            return null;
        }

        private void CheckBlacklists(string ip)
        {
            if (!IPAddress.TryParse(ip, out IPAddress addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            {
                _blacklist = "n/a (IPv6)";
                _blacklistListed = -1;
                return;
            }

            _blacklist = "checking…";
            string reversed = string.Join(".", ip.Split('.').Reverse());

            Task<int>[] checks = Dnsbls.Select(zone => Task.Run(() => Query(reversed + "." + zone))).ToArray();
            Task.WaitAll(checks, 15000);

            int answered = 0, listed = 0;
            foreach (Task<int> t in checks)
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result < 0) continue;
                answered++;
                listed += t.Result;
            }

            _blacklistListed = answered == 0 ? -1 : listed;
            _blacklist = answered == 0 ? "unavailable" : $"{listed}/{answered}";
        }

        /// <returns>1 listed, 0 not listed, -1 no usable answer.</returns>
        private static int Query(string host)
        {
            try
            {
                IPAddress[] answers = Dns.GetHostAddresses(host);
                foreach (IPAddress a in answers)
                {
                    byte[] b = a.GetAddressBytes();
                    if (b.Length != 4 || b[0] != 127) continue;
                    if (b[1] == 255) return -1;   // refused / rate-limited
                    return 1;
                }
                return -1;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
            {
                return 0; // NXDOMAIN: not listed
            }
            catch
            {
                return -1;
            }
        }

        private void ReadFirewall()
        {
            object policy = null;
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", false);
                if (t == null) return;
                policy = Activator.CreateInstance(t);

                int profiles = Convert.ToInt32(t.InvokeMember("CurrentProfileTypes", BindingFlags.GetProperty, null, policy, null));
                bool allOn = true;
                bool any = false;
                foreach (int bit in new[] { 1, 2, 4 })
                {
                    if ((profiles & bit) == 0) continue;
                    any = true;
                    bool on = Convert.ToBoolean(t.InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, new object[] { bit }));
                    allOn &= on;
                }

                _firewallOn = any ? (object)allOn : null;
                _firewallProfile = string.Join("+", new[] { (1, "Domain"), (2, "Private"), (4, "Public") }
                                                    .Where(p => (profiles & p.Item1) != 0).Select(p => p.Item2));
            }
            catch (Exception ex)
            {
                Log.Error("Firewall", ex);
                _firewallOn = null;
                _firewallProfile = null;
            }
            finally
            {
                if (policy != null && System.Runtime.InteropServices.Marshal.IsComObject(policy))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(policy);
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}
