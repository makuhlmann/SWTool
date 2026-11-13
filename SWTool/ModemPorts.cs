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
                        Add(list, com, name, IsSierra(name, id) ? 100 : 50);
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
                        Add(list, m.Groups[1].Value, desc, rank);
                    }
                }
            }
            catch { }

            try
            {
                foreach (string p in SerialPort.GetPortNames()) Add(list, p, "", 5);
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

        private static void Add(List<PortInfo> list, string port, string description, int rank)
        {
            port = port.Trim().ToUpperInvariant();
            foreach (PortInfo p in list)
            {
                if (p.Port != port) continue;
                if (rank > p.Rank) { p.Rank = rank; if (description.Length > 0) p.Description = description; }
                return;
            }
            PortInfo info = new PortInfo();
            info.Port = port;
            info.Description = description;
            info.Rank = rank;
            list.Add(info);
        }
    }
}
