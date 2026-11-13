using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace SWTool
{
    /// <summary>Parsers for the AT command responses of Sierra Wireless (Qualcomm based) modules.</summary>
    internal static class Parsers
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Columns of !GSTATUS are separated by tabs; also accept runs of spaces in front of the next "key:".
        private static readonly Regex SegSplit =
            new Regex(@"\t+|\s{2,}(?=[A-Za-z](?:[A-Za-z0-9()/]| (?! ))*:)", RegexOptions.Compiled);

        public static double? Num(string s)
        {
            if (s == null) return null;
            Match m = Regex.Match(s, @"-?\d+(?:\.\d+)?");
            double d;
            if (m.Success && double.TryParse(m.Value, NumberStyles.Float, Inv, out d)) return d;
            return null;
        }

        private static int? Int(string s)
        {
            int i;
            if (s != null && int.TryParse(s.Trim(), NumberStyles.Integer, Inv, out i)) return i;
            return null;
        }

        public static string Collapse(string s)
        {
            return s == null ? null : Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static string Fmt(double? v, string unit)
        {
            return v.HasValue ? v.Value.ToString("0.#", Inv) + " " + unit : "-";
        }

        // ------------------------------------------------------------------ AT!GSTATUS?

        public static void ParseGStatus(string text, ModemStatus s)
        {
            List<KeyValuePair<string, string>> kv = s.Details;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd();
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("!GSTATUS", StringComparison.Ordinal)) continue;

                string firstKey = null;
                foreach (string rawSeg in SegSplit.Split(line))
                {
                    string seg = rawSeg.Trim();
                    if (seg.Length == 0) continue;

                    int c = seg.IndexOf(':');
                    if (c <= 0)
                    {
                        // Continuation of the previous value ("EMM state: Registered <tab> Normal Service").
                        if (kv.Count > 0)
                        {
                            KeyValuePair<string, string> prev = kv[kv.Count - 1];
                            kv[kv.Count - 1] = new KeyValuePair<string, string>(prev.Key,
                                prev.Value.Length > 0 ? prev.Value + ", " + seg : seg);
                        }
                        continue;
                    }

                    string key = seg.Substring(0, c).Trim();
                    string val = Regex.Replace(seg.Substring(c + 1).Trim(), @"\s{2,}", ", ");

                    if (firstKey == null)
                    {
                        firstKey = key;
                    }
                    else if (key.StartsWith("RSRP", StringComparison.OrdinalIgnoreCase))
                    {
                        // "PCC RxM RSSI: -56 <tab> RSRP (dBm): -95" -> the RSRP belongs to that receive path.
                        Match pm = Regex.Match(firstKey, @"^(.*Rx[MD])\b");
                        if (pm.Success) key = pm.Groups[1].Value + " " + key;
                    }

                    string unique = key;
                    int n = 2;
                    while (s.Detail(unique) != null) unique = key + " #" + n++;
                    kv.Add(new KeyValuePair<string, string>(unique, val));
                }
            }

            s.SystemMode = s.Detail("System mode");
            s.ModemMode = s.Detail("Mode");
            s.PsState = s.Detail("PS state");
            s.Temperature = Num(s.Detail("Temperature"));
            s.RrcState = s.Detail("RRC state");
            s.ImsState = s.Detail("IMS reg state");
            s.CellIdText = s.Detail("Cell ID");
            s.CellId = ParseHexDec(s.CellIdText);
            s.TxPower = Num(s.Detail("Tx Power"));

            if (s.IsLte)
            {
                s.Band = s.Detail("LTE band");
                s.Bandwidth = s.Detail("LTE bw");
                s.RxChannel = s.Detail("LTE Rx chan");
                s.TxChannel = s.Detail("LTE Tx chan");
                s.CaState = s.Detail("LTE CA state");
                s.ServiceState = s.Detail("EMM state");
                s.AreaCode = s.Detail("TAC");
                s.AreaCodeName = "TAC";
                s.Rsrq = Num(s.Detail("RSRQ (dB)"));
                s.Sinr = Num(s.Detail("SINR (dB)"));
            }
            else
            {
                s.Band = s.Detail("WCDMA band") ?? s.Detail("GSM band");
                s.RxChannel = s.Detail("WCDMA channel") ?? s.Detail("GSM channel");
                s.ServiceState = s.Detail("GMM (PS) state") ?? s.Detail("MM (CS) state");
                s.AreaCode = s.Detail("LAC");
                s.AreaCodeName = "LAC";
            }
            // 4294967295 (0xFFFFFFFF) is printed while the transmitter is idle.
            if (s.TxChannel != null && s.TxChannel.StartsWith("4294967295", StringComparison.Ordinal)) s.TxChannel = null;

            // Receive paths: "PCC RxM RSSI" / "PCC RxD RSSI" (LTE), "RxM RSSI C0" / "RxD RSSI C0" (WCDMA).
            foreach (KeyValuePair<string, string> item in kv)
            {
                Match m = Regex.Match(item.Key, @"^(?:(\w+) )?Rx([MD]) RSSI(?: (C\d))?$");
                if (!m.Success) continue;
                RxPath p = new RxPath();
                string carrier = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
                p.Name = (carrier.Length > 0 ? carrier + " " : "") + (m.Groups[2].Value == "M" ? "main" : "diversity");
                p.Rssi = Sane(Num(item.Value));
                if (m.Groups[1].Success)
                    p.Rsrp = Sane(Num(s.Detail(m.Groups[1].Value + " Rx" + m.Groups[2].Value + " RSRP (dBm)")));
                s.Paths.Add(p);

                bool primary = !m.Groups[1].Success ? (m.Groups[3].Value == "C0" || !m.Groups[3].Success)
                    : m.Groups[1].Value == "PCC";
                if (!primary) continue;
                if (p.Rssi.HasValue && (!s.Rssi.HasValue || p.Rssi > s.Rssi)) s.Rssi = p.Rssi;
                if (p.Rsrp.HasValue && (!s.Rsrp.HasValue || p.Rsrp > s.Rsrp)) s.Rsrp = p.Rsrp;
            }
        }

        private static double? Sane(double? v)
        {
            // The firmware reports floor values such as -140/-32768 for unused receive paths.
            return v.HasValue && v.Value > -140 && v.Value < 0 ? v : null;
        }

        /// <summary>Parses "0F56783D (257325117)" style values.</summary>
        private static long? ParseHexDec(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            long v;
            Match m = Regex.Match(s, @"\((\d+)\)");
            if (m.Success && long.TryParse(m.Groups[1].Value, out v)) return v;
            m = Regex.Match(s, @"^[0-9A-Fa-f]+");
            if (m.Success && long.TryParse(m.Value, NumberStyles.HexNumber, Inv, out v)) return v;
            return null;
        }

        // ------------------------------------------------------------------ AT!LTEINFO?

        public static void ParseLteInfo(string text, ModemStatus s)
        {
            string section = null;
            string[] header = null;
            string servingChannel = "", servingPci = null;
            char[] ws = { ' ', '\t' };

            foreach (string line in text.Split('\n'))
            {
                Match h = Regex.Match(line, @"^\s*([A-Za-z]+):\s*(.*)$");
                if (h.Success)
                {
                    section = h.Groups[1].Value;
                    header = h.Groups[2].Value.Split(ws, StringSplitOptions.RemoveEmptyEntries);
                    continue;
                }
                if (section == null) continue;
                string[] tok = line.Split(ws, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length == 0) continue;

                // Rows are right aligned under the header (IntraFreq rows only fill the last columns).
                Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int offset = Math.Max(0, header.Length - tok.Length);
                for (int i = 0; i < tok.Length && i + offset < header.Length; i++) row[header[i + offset]] = tok[i];

                CellInfo cell = new CellInfo();
                switch (section.ToLowerInvariant())
                {
                    case "serving":
                        servingChannel = Get(row, "EARFCN");
                        servingPci = Get(row, "PCI");
                        s.Mcc = Get(row, "MCC");
                        s.Mnc = Get(row, "MNC");
                        s.Pci = Int(servingPci);
                        if (s.RxChannel == null) s.RxChannel = servingChannel;
                        if (!s.Rsrp.HasValue) s.Rsrp = Num(Get(row, "RSRP"));
                        if (!s.Rsrq.HasValue) s.Rsrq = Num(Get(row, "RSRQ"));
                        if (!s.Sinr.HasValue) s.Sinr = Num(Get(row, "SNR"));
                        cell.Kind = "Serving cell";
                        cell.Channel = servingChannel;
                        FillLte(cell, row);
                        break;
                    case "intrafreq":
                        if (Get(row, "PCI") == servingPci) continue; // the serving cell is listed again here
                        cell.Kind = "LTE neighbour (same frequency)";
                        cell.Channel = servingChannel;
                        FillLte(cell, row);
                        break;
                    case "interfreq":
                        cell.Kind = "LTE neighbour (other frequency)";
                        cell.Channel = Get(row, "EARFCN");
                        FillLte(cell, row);
                        break;
                    case "wcdma":
                        cell.Kind = "UMTS neighbour";
                        cell.Channel = Get(row, "UARFCN");
                        cell.Id = Get(row, "PSC");
                        cell.Level = Get(row, "RSCP");
                        cell.Qual = Get(row, "ECN0");
                        break;
                    default:
                        cell.Kind = section + " neighbour";
                        cell.Channel = Get(row, "ARFCN");
                        cell.Id = Get(row, "BSIC");
                        cell.Rssi = Get(row, "RSSI");
                        break;
                }
                s.Cells.Add(cell);
            }
        }

        private static void FillLte(CellInfo cell, Dictionary<string, string> row)
        {
            cell.Id = Get(row, "PCI");
            cell.Level = Get(row, "RSRP");
            cell.Qual = Get(row, "RSRQ");
            cell.Rssi = Get(row, "RSSI");
            int? earfcn = Int(cell.Channel);
            if (earfcn.HasValue)
            {
                int band; double mhz;
                if (LteBands.FromEarfcn(earfcn.Value, out band, out mhz)) cell.Band = "B" + band;
            }
        }

        private static string Get(Dictionary<string, string> row, string key)
        {
            string v;
            return row.TryGetValue(key, out v) ? v : "";
        }

        // ------------------------------------------------------------------ standard 3GPP commands

        /// <summary>Parses +COPS? and returns the name format (0/1 = alphanumeric, 2 = numeric), or -1.</summary>
        public static int ParseCops(string text, ModemStatus s)
        {
            // The operator name can contain line breaks, so match across lines.
            Match m = Regex.Match(text, @"\+COPS:\s*(\d+)(?:\s*,\s*(\d+)\s*,\s*""([^""]*)""(?:\s*,\s*(\d+))?)?");
            if (!m.Success) return -1;
            s.CopsMode = int.Parse(m.Groups[1].Value, Inv);
            if (!m.Groups[2].Success) return -1;
            int format = int.Parse(m.Groups[2].Value, Inv);
            string value = Collapse(m.Groups[3].Value);
            if (format == 2) s.Plmn = value; else s.OperatorName = value;
            if (m.Groups[4].Success) s.Act = int.Parse(m.Groups[4].Value, Inv);
            return format;
        }

        public static List<NetworkEntry> ParseScan(string text)
        {
            List<NetworkEntry> list = new List<NetworkEntry>();
            foreach (Match m in Regex.Matches(text,
                @"\(\s*(\d)\s*,\s*""([^""]*)""\s*,\s*""([^""]*)""\s*,\s*""(\d+)""(?:\s*,\s*(\d+))?\s*\)"))
            {
                NetworkEntry n = new NetworkEntry();
                n.Stat = int.Parse(m.Groups[1].Value, Inv);
                n.LongName = Collapse(m.Groups[2].Value);
                n.ShortName = Collapse(m.Groups[3].Value);
                n.Plmn = m.Groups[4].Value;
                n.Act = m.Groups[5].Success ? int.Parse(m.Groups[5].Value, Inv) : -1;
                list.Add(n);
            }
            return list;
        }

        public static void ParseCsq(string text, ModemStatus s)
        {
            Match m = Regex.Match(text, @"\+CSQ:\s*(\d+)(?:\s*,\s*(\d+))?");
            if (!m.Success) return;
            if (m.Groups[2].Success)
            {
                int ber = int.Parse(m.Groups[2].Value, Inv);
                if (ber <= 7) s.Ber = ber;
            }
            int v = int.Parse(m.Groups[1].Value, Inv);
            if (v > 31) return;
            s.Csq = v;
            if (!s.Rssi.HasValue) s.Rssi = -113 + 2 * v;
        }

        /// <summary>+CESQ: rxlev,ber,rscp,ecno,rsrq,rsrp (3GPP TS 27.007). Used where !GSTATUS has no value.</summary>
        public static void ParseCesq(string text, ModemStatus s)
        {
            Match m = Regex.Match(text, @"\+CESQ:\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
            if (!m.Success) return;
            int rscp = int.Parse(m.Groups[3].Value, Inv), ecno = int.Parse(m.Groups[4].Value, Inv);
            int rsrq = int.Parse(m.Groups[5].Value, Inv), rsrp = int.Parse(m.Groups[6].Value, Inv);
            if (rscp <= 96 && !s.Rscp.HasValue) s.Rscp = -121 + rscp;
            if (ecno <= 49 && !s.Ecio.HasValue) s.Ecio = -24.5 + 0.5 * ecno;
            if (rsrq <= 34 && !s.Rsrq.HasValue) s.Rsrq = -20 + 0.5 * rsrq;
            if (rsrp <= 97 && !s.Rsrp.HasValue) s.Rsrp = -141 + rsrp;
        }

        /// <summary>Returns the stat field of +CREG / +CGREG / +CEREG, or -1.</summary>
        public static int ParseReg(string text)
        {
            return ParseRegInfo(text).Stat;
        }

        /// <summary>
        /// Parses the answer to +CREG? / +CGREG? including the location fields of mode 2:
        /// "+CGREG: 2,5,"3984","0000EA26",3,"01"" (n, stat, lac, ci, act, rac).
        /// </summary>
        public static RegInfo ParseRegInfo(string text)
        {
            RegInfo info = new RegInfo();
            Match m = Regex.Match(text, @"\+C[EG]?REG:\s*\d+\s*,\s*(\d+)"
                + @"(?:\s*,\s*""?([0-9A-Fa-f]+)""?\s*,\s*""?([0-9A-Fa-f]+)""?(?:\s*,\s*(\d+))?(?:\s*,\s*""?([0-9A-Fa-f]+)""?)?)?");
            if (!m.Success) return info;
            info.Stat = int.Parse(m.Groups[1].Value, Inv);
            long v;
            if (m.Groups[2].Success && long.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, Inv, out v)) info.Lac = v;
            if (m.Groups[3].Success && long.TryParse(m.Groups[3].Value, NumberStyles.HexNumber, Inv, out v)) info.Ci = v;
            if (m.Groups[4].Success) info.Act = int.Parse(m.Groups[4].Value, Inv);
            if (m.Groups[5].Success) info.Rac = m.Groups[5].Value.ToUpperInvariant();
            return info;
        }

        /// <summary>"3984 (14724)": hexadecimal as the network counts it, decimal as most tools show it.</summary>
        public static string HexDec(long value, int hexDigits)
        {
            return value.ToString("X" + hexDigits, Inv) + " (" + value.ToString(Inv) + ")";
        }

        /// <summary>Name of the refinement an access technology number implies, or null.</summary>
        public static string SubTechOf(int act)
        {
            switch (act)
            {
                case 3: return "EDGE";
                case 4: return "HSDPA";
                case 5: return "HSUPA";
                case 6: return "HSPA";
                default: return null;
            }
        }

        /// <summary>Decodes EF-ICCID as returned by AT+CRSM=176,12258,0,0,10 (digits are nibble swapped).</summary>
        public static string ParseIccidFile(string text)
        {
            Match m = Regex.Match(text, @"\+CRSM:\s*144\s*,\s*\d+\s*,\s*""?([0-9A-Fa-f]{18,20})""?");
            if (!m.Success) return null;
            string raw = m.Groups[1].Value.ToUpperInvariant();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i + 1 < raw.Length; i += 2) sb.Append(raw[i + 1]).Append(raw[i]);
            string digits = sb.ToString().TrimEnd('F');
            return Regex.IsMatch(digits, @"^\d{18,20}$") ? digits : null;
        }

        // ------------------------------------------------------------------ Icera (%-commands)

        /// <summary>"%NWSTATE: 4,22288,2G-EDGE,-,0": signal bars, PLMN, technology, HSPA state, extra.</summary>
        public static bool ParseNwState(string text, ModemStatus s)
        {
            Match m = Regex.Match(text, @"%I?NWSTATE:\s*(\d+)\s*,\s*(\d*)\s*,\s*([^,\n]*),\s*([^,\n]*)");
            if (!m.Success) return false;
            string tech = m.Groups[3].Value.Trim();
            string upper = tech.ToUpperInvariant();
            if (upper.StartsWith("2G", StringComparison.Ordinal)) s.SystemMode = "GSM";
            else if (upper.StartsWith("3G", StringComparison.Ordinal)) s.SystemMode = "WCDMA";

            if (upper.Contains("HSPA+")) s.SubTech = "HSPA+";
            else if (upper.Contains("HSPA") || (upper.Contains("HSDPA") && upper.Contains("HSUPA"))) s.SubTech = "HSPA";
            else if (upper.Contains("HSDPA")) s.SubTech = "HSDPA";
            else if (upper.Contains("HSUPA")) s.SubTech = "HSUPA";
            else if (upper.Contains("EDGE")) s.SubTech = "EDGE";
            else if (upper.Contains("GPRS")) s.SubTech = "GPRS";

            // "00000" is reported while the modem is not on any network.
            string plmn = m.Groups[2].Value;
            if (plmn.Length >= 5 && plmn.Trim('0').Length > 0 && string.IsNullOrEmpty(s.Plmn)) s.Plmn = plmn;
            s.Details.Add(new KeyValuePair<string, string>("Technology", tech.Length > 0 && tech != "-" ? tech : "none"));
            s.Details.Add(new KeyValuePair<string, string>("Signal bars (modem's own rating)", m.Groups[1].Value + " of 5"));
            string hs = m.Groups[4].Value.Trim();
            if (hs.Length > 0 && hs != "-") s.Details.Add(new KeyValuePair<string, string>("Connection state", hs));
            return true;
        }

        /// <summary>"%ICSQ: 2G-RSSI= -63 dBm" on GSM; RSCP and Ec/N0 values on UMTS.</summary>
        public static void ParseIcsq(string text, ModemStatus s)
        {
            Match line = Regex.Match(text, @"%ICSQ:\s*(.+)");
            if (!line.Success) return;
            s.Details.Add(new KeyValuePair<string, string>("Signal measurement (%ICSQ)", line.Groups[1].Value.Trim()));
            foreach (Match m in Regex.Matches(line.Groups[1].Value, @"(RSSI|RSCP|EC\s*/?\s*N0|EC\s*/?\s*IO)\s*[=:]\s*(-?\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase))
            {
                double v = double.Parse(m.Groups[2].Value, Inv);
                string name = m.Groups[1].Value.ToUpperInvariant();
                if (name == "RSSI") { if (v < 0) s.Rssi = v; }
                else if (name == "RSCP") { if (v < 0) s.Rscp = v; }
                else if (v <= 0) s.Ecio = v;
            }
        }

        /// <summary>
        /// "%INCELL: 3,6,965012009,53,964952064,38,...": count, then pairs of cell identity and receive
        /// level, strongest first. The identity is (LAC * 65536 + cell ID); the cell ID part stays 0
        /// until the modem has read that cell's system information.
        /// </summary>
        public static void ParseIncell(string text, ModemStatus s, long? servingLac, long? servingCi)
        {
            Match m = Regex.Match(text, @"%INCELL:\s*(\d+)\s*,\s*(\d+)((?:\s*,\s*\d+)*)");
            if (!m.Success) return;
            string[] v = m.Groups[3].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < v.Length; i += 2)
            {
                long id, level;
                if (!long.TryParse(v[i].Trim(), out id) || !long.TryParse(v[i + 1].Trim(), out level)) continue;
                long lac = id >> 16, ci = id & 0xFFFF;
                if (servingCi.HasValue && ci == servingCi.Value && lac == (servingLac ?? lac)) continue; // shown as serving cell

                CellInfo c = new CellInfo();
                c.Kind = s.IsWcdma ? "Measured cell" : "Measured GSM cell";
                c.Id = ci != 0 ? ci.ToString(Inv) : "(not read yet)";
                c.Area = lac != 0 ? lac.ToString(Inv) : "";
                // GSM receive level: 0..63 maps to -110..-47 dBm.
                c.Level = s.IsWcdma ? level.ToString(Inv) : (level - 110).ToString(Inv);
                s.Cells.Add(c);
            }
        }

        /// <summary>Parses the band switches of AT%IPBM?: one line per band, "EGSM": 1.</summary>
        public static List<BandToggle> ParseIpbm(string text)
        {
            List<BandToggle> list = new List<BandToggle>();
            foreach (Match m in Regex.Matches(text, @"""([A-Za-z0-9_]+)""\s*:\s*([01])"))
            {
                BandToggle b = new BandToggle();
                b.Id = m.Groups[1].Value;
                b.Enabled = m.Groups[2].Value == "1";
                list.Add(b);
            }
            return list;
        }

        /// <summary>Expands a 3GPP style value list such as "(0-3,5)" into its members.</summary>
        public static List<int> ParseRange(string group)
        {
            List<int> list = new List<int>();
            foreach (string part in group.Trim('(', ')', ' ').Split(','))
            {
                Match m = Regex.Match(part, @"^\s*(\d+)(?:\s*-\s*(\d+))?\s*$");
                if (!m.Success) continue;
                int from = int.Parse(m.Groups[1].Value, Inv);
                int to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, Inv) : from;
                for (int i = from; i <= to && i - from < 64; i++) list.Add(i);
            }
            return list;
        }

        // ------------------------------------------------------------------ packet data (standard commands)

        /// <summary>APN of a context from +CGDCONT? ("+CGDCONT: 1,"IP","internet",...").</summary>
        public static string ApnOfContext(string cgdcont, string cid)
        {
            Match m = Regex.Match(cgdcont, @"\+CGDCONT:\s*" + Regex.Escape(cid) + @"\s*,\s*""[^""]*""\s*,\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>First context reported as active by +CGACT? or %IPDPACT? ("...: cid,1").</summary>
        public static string ActiveContext(string text)
        {
            Match m = Regex.Match(text, @"(?:\+CGACT|%IPDPACT):\s*(\d+)\s*,\s*1\b");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>IPv4 address of a context from +CGPADDR or %IPDPADDR ("...: cid,a.b.c.d,...").</summary>
        public static string AddressOfContext(string text, string cid)
        {
            Match m = Regex.Match(text, @"(?:\+CGPADDR|%IPDPADDR):\s*" + Regex.Escape(cid) + @"\s*,\s*""?(\d+\.\d+\.\d+\.\d+)""?");
            return m.Success && m.Groups[1].Value != "0.0.0.0" ? m.Groups[1].Value : null;
        }

        /// <summary>+CGCONTRDP: cid,bearer,apn,ipv4,ipv6,... (addresses in dotted decimal).</summary>
        public static void ParseContext(string text, ModemStatus s)
        {
            Match m = Regex.Match(text, @"\+CGCONTRDP:\s*(.+)");
            if (!m.Success) return;
            string[] parts = m.Groups[1].Value.Split(',');
            if (parts.Length > 2) s.Apn = parts[2].Trim().Trim('"');
            for (int i = 3; i < parts.Length && i < 5; i++)
            {
                string a = parts[i].Trim().Trim('"');
                int dots = a.Split('.').Length;
                if (dots == 4 && a != "0.0.0.0" && s.Ipv4 == null) s.Ipv4 = a;
                else if (dots == 16 && s.Ipv6 == null) s.Ipv6 = DottedToIpv6(a);
            }
        }

        private static string DottedToIpv6(string dotted)
        {
            try
            {
                string[] p = dotted.Split('.');
                byte[] b = new byte[16];
                bool any = false;
                for (int i = 0; i < 16; i++) { b[i] = byte.Parse(p[i], Inv); any |= b[i] != 0; }
                return any ? new IPAddress(b).ToString() : null;
            }
            catch { return null; }
        }

        /// <summary>Parses "idx, name" lists such as AT!SELRAT=? and AT!BAND=?.</summary>
        public static List<Choice> ParseChoices(string text)
        {
            List<Choice> list = new List<Choice>();
            foreach (string line in text.Split('\n'))
            {
                Match m = Regex.Match(line, @"^\s*(?:![A-Z]+:\s*)?([0-9A-Fa-f]{1,2})\s*,\s*(.+?)\s*$");
                if (!m.Success) continue;
                // Some firmware versions append the band masks after the name.
                string name = Regex.Replace(m.Groups[2].Value, @"[,\s]+[0-9A-Fa-f]{8,}.*$", "").Trim().TrimEnd(',').Trim();
                if (name.Length == 0 || name.IndexOf("Not Supported", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Choice c = new Choice();
                c.Index = m.Groups[1].Value.ToUpperInvariant().PadLeft(2, '0');
                c.Name = name;
                list.Add(c);
            }
            return list;
        }

        public static void ParseAti(string text, DeviceInfo d)
        {
            foreach (string line in text.Split('\n'))
            {
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string key = line.Substring(0, c).Trim().ToUpperInvariant();
                string val = line.Substring(c + 1).Trim();
                if (key == "MANUFACTURER") d.Manufacturer = val;
                else if (key == "MODEL") d.Model = val;
                else if (key == "REVISION") d.Revision = val;
                else if (key == "IMEI") d.Imei = val;
                else if (key == "FSN") d.Serial = val;
            }
        }

        public static string AccessTechnology(int act)
        {
            switch (act)
            {
                case 0: case 1: case 3: return "GSM (2G)";
                case 2: case 4: case 5: case 6: return "UMTS (3G)";
                case 7: return "LTE (4G)";
                default: return "Unknown";
            }
        }

        public static string FormatPlmn(string plmn)
        {
            return plmn != null && plmn.Length >= 5 ? plmn.Substring(0, 3) + " " + plmn.Substring(3) : plmn ?? "";
        }
    }

    /// <summary>LTE band table: downlink EARFCN ranges and frequencies (3GPP TS 36.101).</summary>
    internal static class LteBands
    {
        // band, first DL EARFCN, last DL EARFCN, lowest DL frequency in MHz, common name
        private static readonly object[][] Table =
        {
            new object[] { 1, 0, 599, 2110.0, "2100 MHz" },
            new object[] { 2, 600, 1199, 1930.0, "1900 MHz" },
            new object[] { 3, 1200, 1949, 1805.0, "1800 MHz" },
            new object[] { 4, 1950, 2399, 2110.0, "AWS 1700/2100 MHz" },
            new object[] { 5, 2400, 2649, 869.0, "850 MHz" },
            new object[] { 7, 2750, 3449, 2620.0, "2600 MHz" },
            new object[] { 8, 3450, 3799, 925.0, "900 MHz" },
            new object[] { 12, 5010, 5179, 729.0, "700 MHz" },
            new object[] { 13, 5180, 5279, 746.0, "700 MHz" },
            new object[] { 17, 5730, 5849, 734.0, "700 MHz" },
            new object[] { 20, 6150, 6449, 791.0, "800 MHz" },
            new object[] { 25, 8040, 8689, 1930.0, "1900 MHz" },
            new object[] { 26, 8690, 9039, 859.0, "850 MHz" },
            new object[] { 28, 9210, 9659, 758.0, "700 MHz" },
            new object[] { 29, 9660, 9769, 717.0, "700 MHz SDL" },
            new object[] { 30, 9770, 9869, 2350.0, "2300 MHz" },
            new object[] { 38, 37750, 38249, 2570.0, "2600 MHz TDD" },
            new object[] { 40, 38650, 39649, 2300.0, "2300 MHz TDD" },
            new object[] { 41, 39650, 41589, 2496.0, "2500 MHz TDD" },
            new object[] { 66, 66436, 67335, 2110.0, "AWS-3 1700/2100 MHz" }
        };

        public static bool FromEarfcn(int earfcn, out int band, out double mhz)
        {
            foreach (object[] r in Table)
            {
                if (earfcn < (int)r[1] || earfcn > (int)r[2]) continue;
                band = (int)r[0];
                mhz = (double)r[3] + 0.1 * (earfcn - (int)r[1]);
                return true;
            }
            band = 0; mhz = 0;
            return false;
        }

        public static string Name(int band)
        {
            foreach (object[] r in Table) if ((int)r[0] == band) return (string)r[4];
            return null;
        }
    }
}
