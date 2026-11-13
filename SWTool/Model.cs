using System;
using System.Collections.Generic;
using System.Drawing;

namespace SWTool
{
    public enum Quality { Unknown, VeryPoor, Poor, Fair, Good, Excellent }

    public sealed class RxPath
    {
        public string Name = "";
        public double? Rssi;
        public double? Rsrp;
    }

    public sealed class CellInfo
    {
        public string Kind = "";
        public string Channel = "";
        public string Band = "";
        public string Id = "";      // PCI or PSC
        public string Level = "";   // RSRP or RSCP
        public string Qual = "";    // RSRQ or Ec/N0
        public string Rssi = "";
    }

    public sealed class NetworkEntry
    {
        public int Stat;            // 0 unknown, 1 available, 2 current, 3 forbidden
        public string LongName = "";
        public string ShortName = "";
        public string Plmn = "";
        public int Act = -1;        // 3GPP access technology, -1 if not reported

        public string StatText
        {
            get
            {
                switch (Stat)
                {
                    case 1: return "Available";
                    case 2: return "Current";
                    case 3: return "Forbidden";
                    default: return "Unknown";
                }
            }
        }
    }

    /// <summary>Outcome of a manual registration, which runs as "select operator" plus an optional "lock type".</summary>
    public sealed class RegisterResult
    {
        /// <summary>Response of the last command that was sent.</summary>
        public AtResponse Response;
        public bool OperatorSelected;
        public bool LockRequested;
        public bool TypeLocked;

        public bool Success { get { return OperatorSelected && (!LockRequested || TypeLocked); } }
    }

    /// <summary>An entry of an index/name list such as AT!SELRAT=? or AT!BAND=?.</summary>
    public sealed class Choice
    {
        public string Index = "";
        public string Name = "";
        public override string ToString() { return Name; }
    }

    public sealed class DeviceInfo
    {
        public string Manufacturer, Model, Revision, Imei, Serial, Imsi, Iccid, OwnNumber;
    }

    /// <summary>One snapshot of everything the poller read from the modem.</summary>
    public sealed class ModemStatus
    {
        public DateTime Time = DateTime.Now;
        public bool Responding = true;
        public bool HasGStatus;

        public string OperatorName, Plmn;
        public int CopsMode = -1;       // 0 automatic, 1 manual, 2 deregistered, 4 manual/automatic
        public int Act = -1;
        public int RegCs = -1, RegPs = -1, RegEps = -1;

        public string SystemMode, ModemMode, PsState, ServiceState, RrcState, CaState, ImsState;
        public string Band, Bandwidth, RxChannel, TxChannel, AreaCode, AreaCodeName, CellIdText;
        public long? CellId;
        public int? Pci;
        public string Mcc, Mnc;

        public double? Temperature, Rssi, Rsrp, Rsrq, Sinr, Rscp, Ecio, TxPower;
        public int? Csq;

        public string Apn, Ipv4, Ipv6;
        public string RatPrefIndex, RatPrefName, BandPrefIndex, BandPrefName;

        public readonly List<RxPath> Paths = new List<RxPath>();
        public readonly List<KeyValuePair<string, string>> Details = new List<KeyValuePair<string, string>>();
        public readonly List<CellInfo> Cells = new List<CellInfo>();

        public string Detail(string key)
        {
            foreach (KeyValuePair<string, string> kv in Details)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        private bool ModeHas(string text)
        {
            return SystemMode != null && SystemMode.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool IsLte
        {
            get { return SystemMode != null ? ModeHas("LTE") : Act == 7; }
        }

        public bool IsWcdma
        {
            get
            {
                if (SystemMode != null) return ModeHas("WCDMA") || ModeHas("UMTS") || ModeHas("HSPA") || ModeHas("HSDPA");
                return Act == 2 || (Act >= 4 && Act <= 6);
            }
        }

        public bool IsGsm
        {
            get
            {
                if (SystemMode != null) return ModeHas("GSM") || ModeHas("EDGE") || ModeHas("GPRS");
                return Act == 0 || Act == 1 || Act == 3;
            }
        }

        /// <summary>Registration state (3GPP stat value) of the domain that matters for the current technology.</summary>
        public int RegStat
        {
            get
            {
                int[] order = IsLte ? new[] { RegEps, RegPs, RegCs } : new[] { RegPs, RegCs, RegEps };
                foreach (int v in order) if (v == 1 || v == 5) return v;
                foreach (int v in order) if (v >= 0) return v;
                return -1;
            }
        }

        public bool Registered
        {
            get
            {
                int r = RegStat;
                if (r >= 0) return r == 1 || r == 5;
                return !string.IsNullOrEmpty(OperatorName);
            }
        }

        public bool Roaming { get { return RegStat == 5; } }

        public string RegText
        {
            get
            {
                switch (RegStat)
                {
                    case 0: return "Not registered";
                    case 1: return "Registered (home network)";
                    case 2: return "Searching...";
                    case 3: return "Registration denied";
                    case 5: return "Registered (roaming)";
                    default: return "Unknown";
                }
            }
        }

        public string NetworkType
        {
            get
            {
                if (IsLte)
                {
                    bool ca = CaState != null && CaState.IndexOf("ACTIVE", StringComparison.OrdinalIgnoreCase) >= 0
                        && CaState.IndexOf("INACTIVE", StringComparison.OrdinalIgnoreCase) < 0;
                    return ca ? "LTE-A (4G+, carrier aggregation)" : "LTE (4G)";
                }
                if (IsWcdma) return "UMTS / HSPA (3G)";
                if (IsGsm) return "GSM (2G)";
                return string.IsNullOrEmpty(SystemMode) ? "Unknown" : SystemMode;
            }
        }

        /// <summary>0 (no service) to 5 bars.</summary>
        public int Bars
        {
            get
            {
                if (!Registered) return 0;
                if (IsLte && Rsrp.HasValue) return Step(Rsrp.Value, -85, -95, -105, -115);
                if (IsWcdma && Rscp.HasValue) return Step(Rscp.Value, -75, -85, -95, -105);
                if (Rssi.HasValue) return Step(Rssi.Value, -70, -80, -90, -100);
                return 0;
            }
        }

        private static int Step(double v, double a, double b, double c, double d)
        {
            return v >= a ? 5 : v >= b ? 4 : v >= c ? 3 : v >= d ? 2 : 1;
        }
    }

    internal static class Rating
    {
        public static Quality Rate(double? v, double excellent, double good, double fair, double poor)
        {
            if (!v.HasValue) return Quality.Unknown;
            double x = v.Value;
            return x >= excellent ? Quality.Excellent : x >= good ? Quality.Good : x >= fair ? Quality.Fair
                : x >= poor ? Quality.Poor : Quality.VeryPoor;
        }

        public static Quality Rsrp(double? v) { return Rate(v, -80, -90, -100, -110); }
        public static Quality Rsrq(double? v) { return Rate(v, -10, -13, -16, -19); }
        public static Quality Sinr(double? v) { return Rate(v, 20, 13, 5, 0); }
        public static Quality Rssi(double? v) { return Rate(v, -65, -75, -85, -95); }
        public static Quality Rscp(double? v) { return Rate(v, -70, -85, -95, -105); }
        public static Quality Ecio(double? v) { return Rate(v, -6, -10, -14, -18); }

        public static Quality FromBars(int bars)
        {
            switch (bars)
            {
                case 5: return Quality.Excellent;
                case 4: return Quality.Good;
                case 3: return Quality.Fair;
                case 2: return Quality.Poor;
                case 1: return Quality.VeryPoor;
                default: return Quality.Unknown;
            }
        }

        public static string Text(Quality q)
        {
            switch (q)
            {
                case Quality.Excellent: return "Excellent";
                case Quality.Good: return "Good";
                case Quality.Fair: return "Fair";
                case Quality.Poor: return "Poor";
                case Quality.VeryPoor: return "Very poor";
                default: return "";
            }
        }

        public static Color Colour(Quality q)
        {
            switch (q)
            {
                case Quality.Excellent: return Color.FromArgb(0x2E, 0x9E, 0x44);
                case Quality.Good: return Color.FromArgb(0x7C, 0xB3, 0x42);
                case Quality.Fair: return Color.FromArgb(0xE8, 0xA8, 0x00);
                case Quality.Poor: return Color.FromArgb(0xE6, 0x6A, 0x1E);
                case Quality.VeryPoor: return Color.FromArgb(0xD0, 0x34, 0x34);
                default: return Color.FromArgb(0x9A, 0x9A, 0x9A);
            }
        }
    }
}
