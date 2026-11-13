using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace SWTool
{
    public sealed class PortInfo
    {
        public string Port = "";
        public string Description = "";
        /// <summary>PnP device instance path, e.g. USB\VID_0421&amp;PID_0634&amp;MI_02\6&amp;3FCAB29&amp;0&amp;0002.</summary>
        public string DeviceId = "";
        /// <summary>Higher is a better candidate for the AT command port.</summary>
        public int Rank;

        public override string ToString()
        {
            return Description.Length > 0 ? Port + "  -  " + Description : Port;
        }
    }

    /// <summary>Finds serial ports that may carry the modem's AT command interface.</summary>
    internal static class ModemPorts
    {
        public static List<PortInfo> Detect()
        {
            List<PortInfo> list = new List<PortInfo>();

            // Windows "Modem" class devices: on Sierra cards this is the AT command interface.
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, AttachedTo, DeviceID FROM Win32_POTSModem"))
                {
                    foreach (ManagementBaseObject mo in s.Get())
                    {
                        string com = mo["AttachedTo"] as string;
                        string name = mo["Name"] as string ?? "Modem";
                        string id = mo["DeviceID"] as string ?? "";
                        if (string.IsNullOrEmpty(com)) continue;
                        Add(list, com, name, IsSierra(name, id) ? 100 : 50, id);
                    }
                }
            }
            catch { }

            // Other named COM ports (AT command ports of cards in non-MBIM mode, DM, NMEA...).
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                {
                    foreach (ManagementBaseObject mo in s.Get())
                    {
                        string name = mo["Name"] as string ?? "";
                        string id = mo["DeviceID"] as string ?? "";
                        Match m = Regex.Match(name, @"\((COM\d+)\)");
                        if (!m.Success) continue;
                        string desc = name.Replace(m.Value, "").Trim();
                        int rank = 10;
                        if (Regex.IsMatch(name, @"\bAT\b|Modem", RegexOptions.IgnoreCase)) rank = 40;
                        if (Regex.IsMatch(name, @"\bDM\b|NMEA|Diagnostic|GNSS|GPS", RegexOptions.IgnoreCase)) rank = 1;
                        if (rank > 1 && IsSierra(name, id)) rank += 30;
                        Add(list, m.Groups[1].Value, desc, rank, id);
                    }
                }
            }
            catch { }

            try
            {
                foreach (string p in SerialPort.GetPortNames()) Add(list, p, "", 5, "");
            }
            catch { }

            list.Sort(delegate(PortInfo a, PortInfo b)
            {
                int c = b.Rank.CompareTo(a.Rank);
                return c != 0 ? c : string.Compare(a.Port, b.Port, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static bool IsSierra(string name, string deviceId)
        {
            return name.IndexOf("Sierra", StringComparison.OrdinalIgnoreCase) >= 0
                || deviceId.IndexOf("VID_1199", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Add(List<PortInfo> list, string port, string description, int rank, string deviceId)
        {
            port = port.Trim().ToUpperInvariant();
            foreach (PortInfo p in list)
            {
                if (p.Port != port) continue;
                if (rank > p.Rank) { p.Rank = rank; if (description.Length > 0) p.Description = description; }
                if (p.DeviceId.Length == 0) p.DeviceId = deviceId;
                return;
            }
            PortInfo info = new PortInfo();
            info.Port = port;
            info.Description = description;
            info.Rank = rank;
            info.DeviceId = deviceId;
            list.Add(info);
        }

        /// <summary>
        /// Other AT capable ports of the same USB device as <paramref name="port"/>, best candidate first.
        /// Sticks with several AT ports keep answering on the others while one is blocked by a long command.
        /// </summary>
        public static List<PortInfo> Siblings(string port)
        {
            List<PortInfo> result = new List<PortInfo>();
            List<PortInfo> all = Detect();
            string key = null;
            foreach (PortInfo p in all)
                if (string.Equals(p.Port, port, StringComparison.OrdinalIgnoreCase)) key = DeviceKey(p.DeviceId);
            if (key == null) return result;

            foreach (PortInfo p in all)
            {
                if (string.Equals(p.Port, port, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Rank <= 1 || DeviceKey(p.DeviceId) != key) continue; // skip diagnostic and GPS ports
                result.Add(p);
            }
            // Plain "AT" ports before the Windows modem device, which a dial-up connection may be using.
            result.Sort(delegate(PortInfo a, PortInfo b) { return a.Rank.CompareTo(b.Rank); });
            return result;
        }

        /// <summary>
        /// Identifies the physical USB device an interface belongs to: vendor and product ID plus the
        /// instance prefix that all interfaces of one composite device share.
        /// </summary>
        private static string DeviceKey(string deviceId)
        {
            Match m = Regex.Match(deviceId ?? "", @"^USB\\(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})&MI_[0-9A-F]{2}\\(.+)&[0-9A-F]{4}$",
                RegexOptions.IgnoreCase);
            return m.Success ? (m.Groups[1].Value + "|" + m.Groups[2].Value).ToUpperInvariant() : null;
        }
    }
}
