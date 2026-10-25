using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeskMeters
{
    // Plain data handed from the sampler thread to the UI thread once per tick.
    // Each snapshot is built fresh and never mutated after it is posted.

    internal sealed class Snapshot
    {
        public DateTime Time;
        public bool IsAdmin;
        public bool PawnIoInstalled;
        public bool SensorsReady;
        public string SensorError;
        public CpuSnap Cpu;
        public GpuSnap Gpu;
        public NetSnap Net;
        public DriveSnap[] Drives;
    }

    internal sealed class CpuSnap
    {
        public string Name;
        public string Vendor;          // "AMD", "Intel" or ""
        public float Total;            // %
        public float ClockMHz;         // 0 when unknown
        public float[] ThreadLoad;     // % per logical processor
        public int[] ThreadCore;       // physical core index (0-based) per logical processor
        public float?[] ThreadTemp;    // °C per logical processor (core, CCD or package temp)
        public int CoreCount;
        public float? PackageTemp;
        public float? PackagePower;
        public ulong RamTotal, RamAvail;
        public ulong PageTotal, PageUsed;
    }

    internal sealed class GpuSnap
    {
        public string Name;
        public string[] AllNames;      // every GPU found, for the picker
        public int SelectedIndex;
        public float? Load;
        public float? MemCtrlLoad;
        public float? VideoLoad;
        public float? Temp;
        public float? HotSpot;
        public float? CoreClock;
        public float? MemClock;
        public float? FanRpm;
        public float? FanPct;
        public float? Power;
        public float? MemUsedMB;
        public float? MemTotalMB;
    }

    internal sealed class NetSnap
    {
        public string AdapterId;
        public string AdapterName;
        public List<KeyValuePair<string, string>> Adapters; // id, display name
        public string IntIp;
        public string ExtIp;
        public double UpBps, DownBps;          // bytes per second
        public long SessionUp, SessionDown;    // bytes since the adapter came up
        public double TotalUp, TotalDown;      // bytes, persisted across restarts
        public bool? FirewallOn;
        public string FirewallProfile;
        public string Blacklist;               // e.g. "0/6" or "checking…"
        public int BlacklistListed = -1;       // -1 unknown
        public long LinkSpeed;                 // bits per second
    }

    internal sealed class DriveSnap
    {
        public string Key;       // stable id for histories: "C:" or a lower-cased UNC path
        public string Letter;    // "C:" (null for a UNC-only network location)
        public string Path;      // what to open in Explorer
        public string Label;
        public bool Ready;       // has media / reachable
        public bool Checking;    // network location not checked yet
        public bool Network;
        public bool Custom;      // network location the user added
        public long Total, Free;
        public float? Busy;      // %
        public bool HasRates;
        public double ReadBps, WriteBps;
    }

    /// <summary>Fixed-size history of samples (UI thread).</summary>
    internal sealed class Ring
    {
        private readonly float[] _data;
        private int _head;

        public int Count { get; private set; }
        public int Capacity => _data.Length;

        public Ring(int capacity) { _data = new float[capacity]; }

        public void Push(float v)
        {
            _data[_head] = v;
            _head = (_head + 1) % _data.Length;
            if (Count < _data.Length) Count++;
        }

        /// <summary>i = 0 is the oldest sample still held.</summary>
        public float this[int i]
        {
            get
            {
                int start = (_head - Count + _data.Length) % _data.Length;
                return _data[(start + i) % _data.Length];
            }
        }

        public float Last => Count == 0 ? 0 : this[Count - 1];

        public float Max(int lastN)
        {
            float m = 0;
            int n = Math.Min(lastN, Count);
            for (int i = Count - n; i < Count; i++)
                if (this[i] > m) m = this[i];
            return m;
        }

        public void Clear() { _head = 0; Count = 0; }
    }

    internal static class Fmt
    {
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;
        private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>1536 → "1.50KB". Three significant figures like the classic gadgets.</summary>
        public static string Bytes(double bytes)
        {
            if (double.IsNaN(bytes) || bytes < 0) bytes = 0;
            int u = 0;
            while (bytes >= 1000 && u < ByteUnits.Length - 1) { bytes /= 1024; u++; }
            return Sig3(bytes) + ByteUnits[u];
        }

        public static string BytesPerSec(double bps) => Bytes(bps) + "/s";

        public static string BitsPerSec(double bytesPerSec)
        {
            double bits = Math.Max(0, bytesPerSec * 8);
            string[] units = { "bit/s", "kbit/s", "Mbit/s", "Gbit/s" };
            int u = 0;
            while (bits >= 1000 && u < units.Length - 1) { bits /= 1000; u++; }
            return Sig3(bits) + units[u];
        }

        public static string LinkSpeed(long bitsPerSec)
        {
            if (bitsPerSec <= 0) return "";
            if (bitsPerSec >= 1_000_000_000) return (bitsPerSec / 1e9).ToString(bitsPerSec % 1_000_000_000 == 0 ? "0" : "0.#", C) + " Gbps";
            return (bitsPerSec / 1e6).ToString("0", C) + " Mbps";
        }

        private static string Sig3(double v)
        {
            if (v < 10) return v.ToString(v == Math.Floor(v) && v < 1 ? "0" : "0.00", C);
            if (v < 100) return v.ToString("0.0", C);
            return v.ToString("0", C);
        }

        public static string Pct(float? v) => v.HasValue ? Math.Round(Clamp(v.Value, 0, 100)).ToString(C) + "%" : "--";

        /// <summary>Show temperatures in Fahrenheit (tray setting).</summary>
        public static volatile bool Fahrenheit;

        public static string Temp(float? c)
        {
            if (!c.HasValue) return "--";
            return Fahrenheit
                ? Math.Round(c.Value * 9 / 5 + 32).ToString(C) + "°F"
                : Math.Round(c.Value).ToString(C) + "°C";
        }

        /// <summary>Temperature without the unit, e.g. for tight rows: "46°".</summary>
        public static string TempShort(float? c)
        {
            if (!c.HasValue) return "";
            return Math.Round(Fahrenheit ? c.Value * 9 / 5 + 32 : c.Value).ToString(C) + "°";
        }

        public static string MHz(float? v) => v.HasValue && v.Value > 0 ? Math.Round(v.Value).ToString(C) + " MHz" : "--";

        public static string Watts(float? v) => v.HasValue ? (v.Value < 10 ? v.Value.ToString("0.0", C) : Math.Round(v.Value).ToString(C)) + " W" : "--";

        public static string Num(double v, string format) => v.ToString(format, C);

        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
