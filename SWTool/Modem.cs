using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SWTool
{
    /// <summary>
    /// High level modem operations on top of the AT channel. All methods block; call them from a worker thread.
    /// Standard 3GPP commands are used for every modem; vendor commands add detail for the known dialects:
    /// Sierra Wireless ("AT!...") and Icera based sticks such as the Nokia 7M-01 ("AT%...").
    /// </summary>
    public sealed class Modem
    {
        public readonly AtChannel At = new AtChannel();
        public DeviceInfo Device = new DeviceInfo();
        public ModemKind Kind = ModemKind.Generic;
        /// <summary>Network type preferences the modem offers (empty if it cannot be set).</summary>
        public List<Choice> RatChoices = new List<Choice>();
        /// <summary>Band presets (Sierra). Icera modems switch bands individually, see ModemStatus.Bands.</summary>
        public List<Choice> BandChoices = new List<Choice>();

        private readonly List<KeyValuePair<string, string>> staticDetails = new List<KeyValuePair<string, string>>();
        private ModemStatus last;
        private int tick;
        private string iceraDomain;
        private string typeCommand = "WS46"; // how a plain Qualcomm modem sets its network type: MODODR, SYSCONFIG or WS46
        private bool bandCommands;           // it has +BNDPRF and +NVRW, so its bands can be switched
        private ulong gwMask, lteMask;       // bands enabled now (2G/3G band preference, LTE NV item 6828)
        private ulong offeredGw, offeredLte; // bands offered for switching

        /// <summary>The modem's bands can be switched one by one (see ModemStatus.Bands).</summary>
        public bool HasBandToggles
        {
            get { return Kind == ModemKind.Icera || (Kind == ModemKind.Qualcomm && bandCommands); }
        }

        /// <summary>Set by <see cref="SetBands"/> when a change was stored that only a restart puts into effect.</summary>
        public bool BandChangeNeedsRestart { get; private set; }

        public void Connect(string port)
        {
            At.Open(port);
            last = null;
            tick = 0;
            ReadStatic();
        }

        public void Close()
        {
            At.Close();
        }

        private AtResponse Query(string command)
        {
            return At.Send(command, 3000, true);
        }

        // ============================================================ identification

        private void ReadStatic()
        {
            DeviceInfo d = new DeviceInfo();
            AtResponse r = Query("ATI");
            if (r.Ok) Parsers.ParseAti(r.Text, d);

            // Modems whose ATI is free text answer the standard identification commands.
            if (d.Manufacturer == null) d.Manufacturer = Lines(Query("AT+CGMI"));
            if (d.Model == null) d.Model = Lines(Query("AT+CGMM"));
            if (d.Revision == null) d.Revision = Lines(Query("AT+CGMR"));
            if (d.Imei == null)
            {
                r = Query("AT+CGSN");
                if (r.Ok) d.Imei = FirstMatch(r.Text, @"\d{14,17}");
            }

            r = Query("AT+CIMI");
            if (r.Ok) d.Imsi = FirstMatch(r.Text, @"\d{10,16}");

            // Reading the SIM file works everywhere; the vendor commands differ per firmware
            // ("!ICCID: NOT IMPLEMENTED" still answers OK, and the Nokia's %ICCID has the digits swapped).
            r = Query("AT+CRSM=176,12258,0,0,10");
            if (r.Ok) d.Iccid = Parsers.ParseIccidFile(r.Text);
            foreach (string cmd in new[] { "AT+ICCID", "AT!ICCID?", "AT+CCID" })
            {
                if (d.Iccid != null) break;
                r = Query(cmd);
                if (r.Ok) d.Iccid = FirstMatch(r.Text, @"\d{18,22}");
            }

            r = Query("AT+CNUM");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"\+CNUM:\s*""[^""]*""\s*,\s*""([^""]+)""");
                if (m.Success) d.OwnNumber = m.Groups[1].Value;
            }
            Device = d;

            RatChoices = new List<Choice>();
            BandChoices = new List<Choice>();
            staticDetails.Clear();
            Kind = DetectKind(d);

            if (Kind == ModemKind.Sierra)
            {
                r = Query("AT!SELRAT=?");
                if (r.Ok) RatChoices = Parsers.ParseChoices(r.Text);
                r = Query("AT!BAND=?");
                if (r.Ok) BandChoices = Parsers.ParseChoices(r.Text);
            }
            else
            {
                // Location fields (LAC or TAC, cell ID, technology) in the registration answers.
                Query("AT+CREG=2");
                Query("AT+CGREG=2");
                if (Kind != ModemKind.Icera) Query("AT+CEREG=2");
            }

            if (Kind == ModemKind.Icera) ReadIceraStatic();
            if (Kind == ModemKind.Qualcomm) ReadQualcommStatic();

            // Connection profiles: which numbers and IP types the modem takes, and whether it keeps sign-in data.
            ApnIds = new List<int>();
            ApnTypes = new List<string>();
            r = Query("AT+CGDCONT=?");
            if (r.Ok) Parsers.ParseApnLimits(r.Text, ApnIds, ApnTypes);
            ApnAuth = ApnTypes.Count > 0 && Query("AT$QCPDPP?").Ok;

            Query("AT+COPS=3,0"); // operator names in long alphanumeric format
        }

        private ModemKind DetectKind(DeviceInfo d)
        {
            if (d.Manufacturer != null && d.Manufacturer.IndexOf("Sierra", StringComparison.OrdinalIgnoreCase) >= 0)
                return ModemKind.Sierra;
            AtResponse r = Query("AT!GSTATUS?");
            if (r.Ok && r.Text.IndexOf("!GSTATUS", StringComparison.Ordinal) >= 0
                && r.Text.IndexOf("NOT IMPLEMENTED", StringComparison.Ordinal) < 0) return ModemKind.Sierra;
            r = Query("AT%IPSYS?");
            if (r.Ok && r.Text.IndexOf("%IPSYS", StringComparison.Ordinal) >= 0) return ModemKind.Icera;
            r = Query("AT$QCSYSMODE?");
            if (r.Ok && r.Text.Trim().Length > 0) return ModemKind.Qualcomm;
            return ModemKind.Generic;
        }

        private void ReadIceraStatic()
        {
            // %IPSYS=<mode>: 0 = 2G only, 1 = 3G only, 2 = prefer 2G, 3 = prefer 3G, 5 = any.
            string[] names = { "GSM only (2G)", "UMTS only (3G)", "GSM preferred", "UMTS preferred", null, "Automatic" };
            List<int> supported = new List<int>(new[] { 0, 1, 2, 3 });
            AtResponse r = Query("AT%IPSYS=?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"%IPSYS:\s*(\([^)]*\))");
                if (m.Success) supported = Parsers.ParseRange(m.Groups[1].Value);
            }
            foreach (int mode in supported)
            {
                if (mode < 0 || mode >= names.Length || names[mode] == null) continue;
                Choice c = new Choice();
                c.Index = mode.ToString(CultureInfo.InvariantCulture);
                c.Name = names[mode];
                RatChoices.Add(c);
            }

            r = Query("AT%IHPLMN?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"%IHPLMN:\s*(\d+)\s*,\s*(\d+)");
                if (m.Success) staticDetails.Add(Pair("Home network (from SIM)", m.Groups[1].Value + " " + m.Groups[2].Value));
            }

            r = Query("AT%IFWR?");
            if (r.Ok)
            {
                foreach (Match m in Regex.Matches(r.Text,
                    @"%IFWR:\s*(.+?)\s+version\s+([\d.\s]+?)\s+number\s+(\d+)\s+version text\s+""([^""]*)"""))
                {
                    staticDetails.Add(Pair("Firmware part: " + m.Groups[1].Value.Trim(),
                        m.Groups[4].Value + "  (version " + m.Groups[2].Value.Replace(" ", "") + ", build " + m.Groups[3].Value + ")"));
                }
            }
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static string FirstMatch(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern);
            return m.Success ? m.Value : null;
        }

        /// <summary>Joins the lines of a free text answer, or null if the command failed.</summary>
        private static string Lines(AtResponse r)
        {
            if (!r.Ok) return null;
            List<string> parts = new List<string>();
            foreach (string line in r.Text.Split('\n'))
            {
                // Some modems prefix the value with the command name ("+CGMI: ...").
                string t = Regex.Replace(line.Trim(), @"^\+[A-Z]+:\s*", "");
                if (t.Length > 0) parts.Add(t);
            }
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : null;
        }

        // ============================================================ polling

        /// <summary>Reads one status snapshot. Slowly changing items are refreshed on every fifth call.</summary>
        public ModemStatus Poll()
        {
            ModemStatus s = new ModemStatus();
            bool slow = last == null || tick % 5 == 0;
            tick++;

            // The first command doubles as the "is the modem still answering" check.
            bool alive;
            if (Kind == ModemKind.Sierra) alive = PollSierra(s);
            else if (Kind == ModemKind.Icera) alive = PollIceraState(s);
            else if (Kind == ModemKind.Qualcomm) alive = PollQualcommState(s);
            else alive = true;

            AtResponse r = alive ? Query("AT+CSQ") : null;
            if (r == null || r.TimedOut) return NotResponding(s);
            if (r.Ok) Parsers.ParseCsq(r.Text, s);

            if (Kind == ModemKind.Sierra)
            {
                r = Query("AT+CESQ");
                if (r.Ok) Parsers.ParseCesq(r.Text, s);
            }

            r = Query("AT+COPS?");
            if (r.Ok && Parsers.ParseCops(r.Text, s) == 2)
                Query("AT+COPS=3,0"); // format was left numeric (e.g. by a manual registration)

            RegInfo cs = Parsers.ParseRegInfo(Text(Query("AT+CREG?")));
            RegInfo ps = Parsers.ParseRegInfo(Text(Query("AT+CGREG?")));
            s.RegCs = cs.Stat;
            s.RegPs = ps.Stat;
            // The Nokia has no LTE and rejects the command.
            RegInfo eps = Kind == ModemKind.Icera ? new RegInfo() : Parsers.ParseRegInfo(Text(Query("AT+CEREG?")));
            s.RegEps = eps.Stat;

            if (Kind != ModemKind.Sierra) ApplyLocation(s, cs, ps, eps);
            if (Kind == ModemKind.Icera) PollIceraCells(s);
            if (Kind == ModemKind.Qualcomm) PollQualcommCells(s);

            if (slow)
            {
                if (s.Plmn == null)
                {
                    Query("AT+COPS=3,2");
                    r = Query("AT+COPS?");
                    if (r.Ok)
                    {
                        ModemStatus tmp = new ModemStatus();
                        Parsers.ParseCops(r.Text, tmp);
                        s.Plmn = tmp.Plmn;
                    }
                    Query("AT+COPS=3,0");
                }

                if (Kind == ModemKind.Sierra) SlowSierra(s);
                else if (Kind == ModemKind.Icera) SlowIcera(s);
                else if (Kind == ModemKind.Qualcomm) SlowQualcomm(s);
                else SlowGeneric(s);
            }
            else
            {
                if (s.Plmn == null) s.Plmn = last.Plmn;
                s.RatPrefIndex = last.RatPrefIndex;
                s.RatPrefName = last.RatPrefName;
                s.BandPrefIndex = last.BandPrefIndex;
                s.BandPrefName = last.BandPrefName;
                s.Bands = last.Bands;
                s.Apn = last.Apn;
                s.Ipv4 = last.Ipv4;
                s.Ipv6 = last.Ipv6;
                if (Kind != ModemKind.Sierra)
                {
                    s.PsState = last.PsState;
                    s.ModemMode = last.ModemMode;
                    foreach (KeyValuePair<string, string> kv in last.slowDetails) s.slowDetails.Add(kv);
                }
            }

            // A command that went unanswered part way through leaves the rest of this snapshot empty.
            if (At.PendingCommand != null) return NotResponding(s);

            if (string.IsNullOrEmpty(s.Plmn) && s.Mcc != null && s.Mcc.Length > 0 && s.Mnc != null) s.Plmn = s.Mcc + s.Mnc;
            if (s.SubTech == null) s.SubTech = Parsers.SubTechOf(s.Act);

            if (Kind != ModemKind.Sierra)
            {
                if (s.Ber.HasValue) s.Details.Add(Pair("Bit error class (RXQUAL, 0 = best)", s.Ber.Value.ToString(CultureInfo.InvariantCulture)));
                s.Details.AddRange(s.slowDetails);
                s.Details.AddRange(staticDetails);
            }

            last = s;
            return s;
        }

        private static string Text(AtResponse r)
        {
            return r.Ok ? r.Text : "";
        }

        /// <summary>Marks a snapshot as incomplete because the modem is busy or silent.</summary>
        private ModemStatus NotResponding(ModemStatus s)
        {
            s.Responding = false;
            s.BusyCommand = At.PendingCommand;
            s.BusySeconds = At.PendingSeconds;
            tick = 0; // read everything again once the modem answers
            return s;
        }

        /// <summary>Fills area code, cell ID and technology from the registration answers (mode 2).</summary>
        private static void ApplyLocation(ModemStatus s, RegInfo cs, RegInfo ps, RegInfo eps)
        {
            // On LTE the tracking area and cell come with the EPS registration.
            bool onLte = eps.Ci.HasValue && (eps.Stat == 1 || eps.Stat == 5);
            RegInfo loc = onLte ? eps : ps.Ci.HasValue ? ps : cs;
            if (loc.Lac.HasValue)
            {
                s.AreaCode = Parsers.HexDec(loc.Lac.Value, 4);
                s.AreaCodeName = onLte ? "TAC" : "LAC";
            }
            if (loc.Ci.HasValue)
            {
                s.CellId = loc.Ci.Value;
                s.CellIdText = Parsers.HexDec(loc.Ci.Value, loc.Ci.Value > 0xFFFF ? 7 : 4);
            }
            s.Rac = ps.Rac;
            if (s.Act < 0) s.Act = loc.Act >= 0 ? loc.Act : cs.Act;

            // Without a vendor status command the technology comes from the access technology number.
            if (s.SystemMode == null && s.Act >= 0)
                s.SystemMode = s.Act == 7 ? "LTE" : (s.Act == 2 || (s.Act >= 4 && s.Act <= 6)) ? "WCDMA" : "GSM";

            s.lac = loc.Lac;
        }

        // ------------------------------------------------------------ Sierra Wireless

        private bool PollSierra(ModemStatus s)
        {
            s.DetailsTitle = "Modem status (AT!GSTATUS?)";
            AtResponse r = Query("AT!GSTATUS?");
            if (r.TimedOut) return false;
            if (r.Ok)
            {
                s.HasGStatus = true;
                Parsers.ParseGStatus(r.Text, s);
            }

            if (!s.HasGStatus || s.IsLte)
            {
                r = Query("AT!LTEINFO?");
                if (r.Ok) Parsers.ParseLteInfo(r.Text, s);
            }
            return true;
        }

        private void SlowSierra(ModemStatus s)
        {
            AtResponse r = Query("AT!SELRAT?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"!SELRAT:\s*([0-9A-Fa-f]+)\s*,\s*(.+)");
                if (m.Success)
                {
                    s.RatPrefIndex = m.Groups[1].Value.ToUpperInvariant().PadLeft(2, '0');
                    s.RatPrefName = m.Groups[2].Value.Trim();
                }
            }

            r = Query("AT!BAND?");
            if (r.Ok)
            {
                List<Choice> current = Parsers.ParseChoices(r.Text);
                if (current.Count > 0)
                {
                    s.BandPrefIndex = current[0].Index;
                    s.BandPrefName = current[0].Name;
                }
            }

            r = Query("AT+CGCONTRDP");
            if (r.Ok) Parsers.ParseContext(r.Text, s);
        }

        // ------------------------------------------------------------ Icera (Nokia 7M-01 and relatives)

        private bool PollIceraState(ModemStatus s)
        {
            s.DetailsTitle = "Modem status (Icera %-commands)";
            // Setting the reporting mode prints the current state once. Switch the reports off again
            // so that they do not trickle in between other answers.
            AtResponse r = Query("AT%NWSTATE=1");
            if (r.TimedOut) return false;
            if (r.Ok) Parsers.ParseNwState(r.Text, s);
            Query("AT%NWSTATE=0");

            r = Query("AT%ICSQ?");
            if (r.Ok) Parsers.ParseIcsq(r.Text, s);
            return true;
        }

        private void PollIceraCells(ModemStatus s)
        {
            if (s.Registered && s.CellId.HasValue)
            {
                CellInfo serving = new CellInfo();
                serving.Kind = "Serving cell";
                serving.Id = s.CellId.Value.ToString(CultureInfo.InvariantCulture);
                serving.Area = s.lac.HasValue ? s.lac.Value.ToString(CultureInfo.InvariantCulture) : "";
                double? level = s.IsWcdma ? s.Rscp : s.Rssi;
                if (level.HasValue) serving.Level = level.Value.ToString("0.#", CultureInfo.InvariantCulture);
                if (s.IsWcdma && s.Ecio.HasValue) serving.Qual = s.Ecio.Value.ToString("0.#", CultureInfo.InvariantCulture);
                s.Cells.Add(serving);
            }
            // %INCELL? makes the modem measure its surroundings. Without a serving cell that measurement
            // takes over a minute, blocks this port for as long, and keeps the radio from finding a
            // network in the meantime; asking again on every poll then locks the modem out for good
            // (the "only unplugging helps" state after a network or band change). So the list is only
            // requested once the registration has held for two polls in a row.
            bool settled = s.Registered && last != null && last.Responding && last.Registered;
            if (!settled) return;
            AtResponse r = Query("AT%INCELL?");
            if (r.Ok) Parsers.ParseIncell(r.Text, s, s.lac, s.CellId);
        }

        private void SlowIcera(ModemStatus s)
        {
            List<KeyValuePair<string, string>> d = s.slowDetails;

            AtResponse r = Query("AT%IPSYS?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"%IPSYS:\s*(\d+)\s*(?:,\s*(\d+))?");
                if (m.Success)
                {
                    s.RatPrefIndex = m.Groups[1].Value;
                    foreach (Choice c in RatChoices) if (c.Index == s.RatPrefIndex) s.RatPrefName = c.Name;
                    if (s.RatPrefName == null) s.RatPrefName = "Mode " + s.RatPrefIndex;
                    iceraDomain = m.Groups[2].Success ? m.Groups[2].Value : null;
                    if (m.Groups[2].Success)
                    {
                        string[] domains = { "Voice / SMS only (CS)", "Packet data only (PS)", "Voice and packet data (CS + PS)" };
                        int dom = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        d.Add(Pair("Service domain", dom < domains.Length ? domains[dom] : "Mode " + dom));
                    }
                }
            }

            r = Query("AT%IPBM?");
            if (r.Ok)
            {
                List<BandToggle> bands = Parsers.ParseIpbm(r.Text);
                if (bands.Count > 0)
                {
                    s.Bands = bands;
                    s.BandPrefName = DescribeBands(bands);
                    d.Add(Pair("Enabled bands", s.BandPrefName));
                }
            }

            r = Query("AT%INWMODE?");
            if (r.Ok)
            {
                List<string> parts = new List<string>();
                foreach (Match m in Regex.Matches(r.Text, @"%INWMODE:\s*(.+)")) parts.Add(m.Groups[1].Value.Trim());
                if (parts.Count > 0) d.Add(Pair("Band search order (%INWMODE)", string.Join("  |  ", parts.ToArray())));
            }

            r = Query("AT+CGCLASS?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"\+CGCLASS:\s*""?([A-Za-z]+)""?");
                if (m.Success) d.Add(Pair("GPRS mobile class", m.Groups[1].Value));
            }

            ReadRadioAndAttach(s);

            // Windows activates the data session with %IPDPACT on this modem type.
            r = Query("AT%IPDPACT?");
            string cid = r.Ok ? Parsers.ActiveContext(r.Text) : null;
            if (cid != null)
            {
                s.Apn = Parsers.ApnOfContext(Text(Query("AT+CGDCONT?")), cid);
                s.Ipv4 = Parsers.AddressOfContext(Text(Query("AT%IPDPADDR?")), cid);
                d.Add(Pair("Active data context", cid));
            }
        }

        private static string DescribeBands(List<BandToggle> bands)
        {
            List<string> on = new List<string>();
            foreach (BandToggle b in bands)
            {
                if (!b.Enabled) continue;
                if (b.Id == "ANY") return b.Name;
                on.Add(b.Name);
            }
            return on.Count > 0 ? string.Join(", ", on.ToArray()) : "none";
        }

        // ------------------------------------------------------------ plain Qualcomm (4G Systems W1208 and relatives)

        private void ReadQualcommStatic()
        {
            // The network type preference is read with the standard +WS46 (12 GSM, 22 UMTS, 25 automatic,
            // 28 LTE, ...). Writing it is another matter: the W1208 answers OK to AT+WS46=<n> and ignores
            // it. What it obeys is its maker's +MODODR (all four types) and the Huawei style ^SYSCONFIG
            // (no "LTE only"), so the best one the modem has is used.
            List<int> values = new List<int>();
            AtResponse r = Query("AT+MODODR=?");
            if (r.Ok && r.Text.IndexOf("+MODODR", StringComparison.Ordinal) >= 0)
            {
                typeCommand = "MODODR";
                values.AddRange(new[] { 25, 28, 22, 12 });
            }
            else if ((r = Query("AT^SYSCONFIG?")).Ok && r.Text.IndexOf("^SYSCONFIG:", StringComparison.Ordinal) >= 0)
            {
                typeCommand = "SYSCONFIG";
                values.AddRange(new[] { 25, 22, 12 });
            }
            else
            {
                typeCommand = "WS46";
                r = Query("AT+WS46=?");
                Match m = r.Ok ? Regex.Match(r.Text, @"\([^)]*\)") : Match.Empty;
                if (m.Success) values = Parsers.ParseRange(m.Value);
            }
            foreach (int value in values)
            {
                Choice c = new Choice();
                c.Index = value.ToString(CultureInfo.InvariantCulture);
                c.Name = Parsers.Ws46Name(value);
                RatChoices.Add(c);
            }

            r = Query("AT+HPLMN");
            if (r.Ok)
            {
                string plmn = FirstMatch(r.Text, @"\d{5,6}");
                if (plmn != null) staticDetails.Add(Pair("Home network (from SIM)", Parsers.FormatPlmn(plmn)));
            }
            r = Query("AT$QCSIMT?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"\$QCSIMT:\s*(\S.*)");
                if (m.Success) staticDetails.Add(Pair("SIM type", m.Groups[1].Value.Trim()));
            }
            string hw = Lines(Query("AT$QCHWREV"));
            if (hw != null) staticDetails.Add(Pair("Hardware revision", hw));

            // Band selection, where the firmware has the means (Longcheer based sticks such as the W1208):
            // +BNDPRF holds the 2G/3G bands and applies at once; the LTE bands are NV item 6828, readable
            // and writable with +NVRW, and only take effect after a restart.
            ulong mask;
            bandCommands = Parsers.ParseBndPrf(Text(Query("AT+BNDPRF?")), out mask)
                && Parsers.ParseNvUInt64(Text(Query("AT+NVRW=0,6828")), out lteMask);
            if (bandCommands)
            {
                gwMask = mask;
                // Offer every band the modem has ever had enabled, so that one switched off can be switched
                // on again. The modem cannot be asked what its hardware supports.
                string key = "Bands_" + (Device.Imei ?? "unknown");
                string[] known = (AppSettings.Get(key) ?? "").Split(',');
                ulong knownGw = 0, knownLte = 0;
                if (known.Length == 2)
                {
                    ulong.TryParse(known[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out knownGw);
                    ulong.TryParse(known[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out knownLte);
                }
                offeredGw = knownGw | gwMask;
                offeredLte = knownLte | lteMask;
                if (offeredGw != knownGw || offeredLte != knownLte)
                    AppSettings.Set(key, offeredGw.ToString("X", CultureInfo.InvariantCulture) + "," + offeredLte.ToString("X", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Builds the band switches of a Longcheer based modem from its two masks.</summary>
        private List<BandToggle> QualcommBands()
        {
            List<BandToggle> list = new List<BandToggle>();
            for (int bit = 0; bit < 64; bit++)
            {
                if ((offeredGw & (1UL << bit)) == 0) continue;
                BandToggle b = new BandToggle();
                b.Id = "GW" + bit.ToString(CultureInfo.InvariantCulture);
                b.Label = Parsers.GwBandName(bit);
                b.Enabled = (gwMask & (1UL << bit)) != 0;
                list.Add(b);
            }
            for (int bit = 0; bit < 64; bit++)
            {
                if ((offeredLte & (1UL << bit)) == 0) continue;
                string name = LteBands.Name(bit + 1);
                BandToggle b = new BandToggle();
                b.Id = "LTE" + (bit + 1).ToString(CultureInfo.InvariantCulture);
                b.Label = "LTE band " + (bit + 1).ToString(CultureInfo.InvariantCulture) + (name != null ? " (" + name + ")" : "");
                b.Enabled = (lteMask & (1UL << bit)) != 0;
                b.NeedsRestart = true;
                list.Add(b);
            }
            return list;
        }

        private static string DescribeQualcommBands(List<BandToggle> bands)
        {
            List<string> gw = new List<string>(), lte = new List<string>();
            foreach (BandToggle b in bands)
            {
                if (!b.Enabled) continue;
                if (b.Id.StartsWith("LTE", StringComparison.Ordinal)) lte.Add("B" + b.Id.Substring(3));
                else gw.Add(b.Name);
            }
            string text = string.Join(", ", gw.ToArray());
            if (lte.Count > 0) text += (text.Length > 0 ? ", " : "") + "LTE " + string.Join(" / ", lte.ToArray());
            return text.Length > 0 ? text : "none";
        }

        private bool PollQualcommState(ModemStatus s)
        {
            s.DetailsTitle = "Modem status (Qualcomm $QC commands)";
            AtResponse r = Query("AT$QCSYSMODE?");
            if (r.TimedOut) return false;
            string mode = Lines(r);
            if (mode != null)
            {
                // "WCDMA", "LTE", "GSM", or with a suffix such as "WCDMA - HSDPA and HSUPA".
                s.SystemMode = mode;
                s.Details.Add(Pair("System mode ($QCSYSMODE)", mode));
                string upper = mode.ToUpperInvariant();
                if (upper.Contains("HSDPA") && upper.Contains("HSUPA")) s.SubTech = "HSPA";
                else if (upper.Contains("HSDPA")) s.SubTech = "HSDPA";
                else if (upper.Contains("HSUPA")) s.SubTech = "HSUPA";
            }

            r = Query("AT*CNTI=0");
            if (r.Ok) Parsers.ParseCnti(r.Text, s);

            r = Query("AT$QCSQ");
            if (r.Ok) Parsers.ParseQcsq(r.Text, s);
            return true;
        }

        private void PollQualcommCells(ModemStatus s)
        {
            // LTE cells first: on LTE the strongest one is the serving cell and supplies PCI, channel and levels.
            AtResponse rsrp = Query("AT$QCRSRP?");
            if (rsrp.Ok) Parsers.ParseQcLteCells(rsrp.Text, Text(Query("AT$QCRSRQ?")), s);

            if (s.IsLte || !s.Registered || !s.CellId.HasValue) return;
            CellInfo serving = new CellInfo();
            serving.Kind = "Serving cell";
            serving.Id = s.CellId.Value.ToString(CultureInfo.InvariantCulture);
            serving.Area = s.lac.HasValue ? s.lac.Value.ToString(CultureInfo.InvariantCulture) : "";
            double? level = s.IsWcdma ? s.Rscp : s.Rssi;
            if (level.HasValue) serving.Level = level.Value.ToString("0.#", CultureInfo.InvariantCulture);
            if (s.IsWcdma && s.Ecio.HasValue) serving.Qual = s.Ecio.Value.ToString("0.#", CultureInfo.InvariantCulture);
            if (s.IsWcdma && s.Rssi.HasValue) serving.Rssi = s.Rssi.Value.ToString("0.#", CultureInfo.InvariantCulture);
            s.Cells.Insert(0, serving);
        }

        private void SlowQualcomm(ModemStatus s)
        {
            List<KeyValuePair<string, string>> d = s.slowDetails;

            AtResponse r = Query("AT+WS46?");
            if (r.Ok)
            {
                string value = FirstMatch(r.Text, @"\d+");
                if (value != null)
                {
                    s.RatPrefIndex = value;
                    s.RatPrefName = Parsers.Ws46Name(int.Parse(value, CultureInfo.InvariantCulture));
                }
            }

            ulong mask, nv;
            if (bandCommands && Parsers.ParseBndPrf(Text(Query("AT+BNDPRF?")), out mask)
                && Parsers.ParseNvUInt64(Text(Query("AT+NVRW=0,6828")), out nv))
            {
                gwMask = mask;
                lteMask = nv;
                s.Bands = QualcommBands();
                s.BandPrefName = DescribeQualcommBands(s.Bands);
                d.Add(Pair("Enabled bands", s.BandPrefName));
                d.Add(Pair("Band masks (+BNDPRF, NV 6828)", Parsers.FormatBndPrf(gwMask) + "  |  " + Parsers.FormatNvUInt64(lteMask)));
            }
            else
            {
                r = Query("AT$QCBANDPREF?");
                if (r.Ok)
                {
                    s.BandPrefName = Parsers.ParseQcBands(r.Text);
                    if (s.BandPrefName != null) d.Add(Pair("Enabled 2G / 3G bands", s.BandPrefName));
                }
            }

            r = Query("AT^SYSINFO");
            if (r.Ok) Parsers.ParseSysInfo(r.Text, d);

            // Why the last attach or registration failed, e.g. "Missing or unknown APN" keeps a modem off LTE.
            r = Query("AT+CEER");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"\+CEER:\s*(\S.*)");
                if (m.Success) d.Add(Pair("Last error reported by the network", m.Groups[1].Value.Trim()));
            }

            ReadRadioAndAttach(s);

            string contexts = Text(Query("AT+CGDCONT?"));
            string list = Parsers.ContextList(contexts);
            if (list != null) d.Add(Pair("APN profiles", list));

            r = Query("AT+CGCONTRDP");
            if (r.Ok && r.Text.IndexOf("+CGCONTRDP", StringComparison.Ordinal) >= 0)
            {
                Parsers.ParseContext(r.Text, s);
                return;
            }
            r = Query("AT+CGACT?");
            string cid = r.Ok ? Parsers.ActiveContext(r.Text) : null;
            if (cid == null) return;
            s.Apn = Parsers.ApnOfContext(contexts, cid);
            s.Ipv4 = Parsers.AddressOfContext(Text(Query("AT+CGPADDR=" + cid)), cid);
        }

        // ------------------------------------------------------------ standard commands only

        private void SlowGeneric(ModemStatus s)
        {
            ReadRadioAndAttach(s);
            AtResponse r = Query("AT+CGCONTRDP");
            if (r.Ok)
            {
                Parsers.ParseContext(r.Text, s);
                return;
            }
            r = Query("AT+CGACT?");
            string cid = r.Ok ? Parsers.ActiveContext(r.Text) : null;
            if (cid == null) return;
            s.Apn = Parsers.ApnOfContext(Text(Query("AT+CGDCONT?")), cid);
            s.Ipv4 = Parsers.AddressOfContext(Text(Query("AT+CGPADDR=" + cid)), cid);
        }

        /// <summary>Radio on/off (+CFUN) and packet service attach state (+CGATT).</summary>
        private void ReadRadioAndAttach(ModemStatus s)
        {
            AtResponse r = Query("AT+CFUN?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"\+CFUN:\s*(\d+)");
                if (m.Success) s.ModemMode = m.Groups[1].Value == "1" ? "ONLINE" : "RADIO OFF (CFUN " + m.Groups[1].Value + ")";
            }
            r = Query("AT+CGATT?");
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"\+CGATT:\s*(\d)");
                if (m.Success) s.PsState = m.Groups[1].Value == "1" ? "Attached" : "Detached";
            }
        }

        /// <summary>Makes the next poll refresh the slowly changing items too.</summary>
        public void InvalidateSlowItems()
        {
            tick = 0;
        }

        // ============================================================ network search and selection

        private volatile bool cancelRequested;

        /// <summary>
        /// Whether a running registration can be interrupted. Qualcomm based modems drop it on any
        /// input (a network search they finish regardless). Icera modems ignore everything on the port
        /// that is busy (input, DTR, break). Their second AT port stays usable, but a command that forces
        /// the issue from there (AT+CFUN=4 was tried) can deadlock the firmware, so they are left to finish.
        /// </summary>
        public bool CanCancel { get { return Kind != ModemKind.Icera; } }

        /// <summary>Searches for available networks (AT+COPS=?). Takes 30 s to 3 min.</summary>
        public List<NetworkEntry> Scan(out AtResponse response)
        {
            response = At.Send("AT+COPS=?", 240000);
            return response.Ok ? Parsers.ParseScan(response.Text) : new List<NetworkEntry>();
        }

        /// <summary>
        /// Qualcomm based modems (EM7455, W1208) drop a registration that is still waiting for the
        /// network as soon as anything is sent to them.
        /// </summary>
        private bool AbortsOnInput
        {
            get { return Kind == ModemKind.Sierra || Kind == ModemKind.Qualcomm; }
        }

        /// <summary>
        /// Whether a registration that names a network type also rewrites the modem's network type
        /// preference to that type only (!SELRAT on the EM7455, +WS46 on the W1208).
        /// </summary>
        public bool RegistrationSetsTypePreference
        {
            get { return Kind == ModemKind.Sierra || Kind == ModemKind.Qualcomm; }
        }

        /// <summary>
        /// How long to wait for a registration. A Qualcomm based modem never answers when the network
        /// refuses the SIM, but lets go when interrupted, so it gets a limit. Other modems (seen on the
        /// Nokia 7M-01) answer by themselves after up to about 100 s, block their port until then and
        /// cannot be interrupted on it, so there is no point in giving up early.
        /// </summary>
        private int RegisterTimeoutMs
        {
            get { return AbortsOnInput ? 100000 : 300000; }
        }

        /// <summary>
        /// Manual registration on a PLMN, optionally locking the modem to one access technology.
        /// </summary>
        /// <remarks>
        /// Always done in two steps because of how the EM7455 firmware treats AT+COPS:
        /// "AT+COPS=1,2,plmn,act" sets the network type preference (!SELRAT) to that type only, and while
        /// the preference is already locked to a type, naming that type again for another network is
        /// answered with "registration denied" and the command hangs. Without a type the command works
        /// from any state and resets the preference to Automatic, so the operator is selected first and
        /// the type is locked afterwards, on the network the modem is then already registered on.
        /// </remarks>
        public RegisterResult Register(string plmn, int act, bool lockType)
        {
            cancelRequested = false;
            int previousMode = last != null ? last.CopsMode : -1;
            string previousPlmn = last != null ? last.Plmn : null;
            string previousType = last != null ? last.RatPrefIndex : null;

            // While it is attached to LTE the W1208 answers a change of operator with "no network service"
            // within half a second, without trying. After an explicit deregistration the same command works.
            if (Kind == ModemKind.Qualcomm && last != null && last.IsLte && last.Registered && last.Plmn != plmn)
                At.Send("AT+COPS=2", 30000);

            RegisterResult res = new RegisterResult();
            AtResponse r = At.Send("AT+COPS=1,2,\"" + plmn + "\"", RegisterTimeoutMs);
            res.Response = r;
            // An interrupted command ends with OK on a Sierra modem, so the answer alone does not tell.
            res.OperatorSelected = r.Ok && !cancelRequested;

            // Send the canonical access technology (HSPA variants are selected as UTRAN).
            int a = act == 7 ? 7 : (act == 2 || (act >= 4 && act <= 6)) ? 2 : act >= 0 ? 0 : -1;
            if (res.OperatorSelected && lockType && a >= 0)
            {
                res.LockRequested = true;
                r = At.Send("AT+COPS=1,2,\"" + plmn + "\"," + a, RegisterTimeoutMs);
                res.Response = r;
                res.TypeLocked = r.Ok && !cancelRequested;
            }

            res.Cancelled = cancelRequested;
            if (!res.Success) RestoreSelection(res, plmn, previousMode, previousPlmn, previousType);

            if (At.PendingCommand == null) Query("AT+COPS=3,0");
            InvalidateSlowItems();
            return res;
        }

        /// <summary>Puts the network selection back after a registration that failed or was cancelled.</summary>
        private void RestoreSelection(RegisterResult res, string plmn, int previousMode, string previousPlmn, string previousType)
        {
            if (AbortsOnInput && At.PendingCommand != null)
            {
                At.Interrupt();
                At.WaitIdle(6000);
            }
            if (At.PendingCommand != null)
            {
                res.StillPending = true; // nothing can be sent on this port until the modem answers
                return;
            }

            if (Kind == ModemKind.Sierra)
            {
                // Once it has dropped the attempt, the EM7455 returns to its previous network by itself.
                res.Reverted = !res.OperatorSelected;
                return;
            }

            if (res.OperatorSelected)
            {
                // Only the type lock failed. The W1208 keeps the type restriction it could not honour
                // (+WS46 stays on e.g. "LTE only") and is then without service, so undo that and select
                // the operator again without a type.
                if (Kind == ModemKind.Qualcomm)
                {
                    RestoreTypePreference(previousType);
                    At.Send("AT+COPS=1,2,\"" + plmn + "\"", RegisterTimeoutMs);
                }
                return;
            }

            // These modems stay in manual mode on the network that refused them, without service.
            if (Kind == ModemKind.Qualcomm) RestoreTypePreference(previousType);
            AtResponse back = previousMode == 1 && !string.IsNullOrEmpty(previousPlmn) && previousPlmn != plmn
                ? At.Send("AT+COPS=1,2,\"" + previousPlmn + "\"", RegisterTimeoutMs)
                : At.Send("AT+COPS=0", 30000);
            res.Reverted = back.Ok;
        }

        /// <summary>Sets the network type preference back to the given +WS46 value if a registration attempt changed it.</summary>
        private void RestoreTypePreference(string previous)
        {
            if (string.IsNullOrEmpty(previous)) return;
            string now = FirstMatch(Text(Query("AT+WS46?")), @"\d+");
            if (now != null && now != previous) SetQualcommType(previous);
        }

        /// <summary>
        /// Sets the network type preference of a plain Qualcomm modem, given as a +WS46 value, and
        /// checks that it took effect.
        /// </summary>
        private AtResponse SetQualcommType(string ws46)
        {
            AtResponse r;
            if (typeCommand == "MODODR")
            {
                // +MODODR=<n>: 2 automatic, 5 LTE only, 1 UMTS only, 3 GSM only (it reads back 15 and 13 for the last two).
                string mode = ws46 == "25" ? "2" : ws46 == "28" ? "5" : ws46 == "22" ? "1" : ws46 == "12" ? "3" : null;
                if (mode == null)
                {
                    r = new AtResponse();
                    r.Command = "AT+MODODR";
                    r.Result = "This modem cannot be set to that network type directly.";
                    return r;
                }
                r = At.Send("AT+MODODR=" + mode, 20000);
            }
            else if (typeCommand == "SYSCONFIG")
            {
                // ^SYSCONFIG=<mode>,<acquisition order>,<roaming>,<service domain>: 2 automatic, 13 GSM only,
                // 14 UMTS only. The other three values are sent back as they are.
                string mode = ws46 == "25" ? "2" : ws46 == "22" ? "14" : ws46 == "12" ? "13" : null;
                Match now = Regex.Match(Text(Query("AT^SYSCONFIG?")), @"\^SYSCONFIG:\s*\d+\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
                if (mode == null || !now.Success)
                {
                    r = new AtResponse();
                    r.Command = "AT^SYSCONFIG";
                    r.Result = mode == null ? "This modem cannot be set to that network type directly."
                        : "The modem did not report its current configuration.";
                    return r;
                }
                r = At.Send("AT^SYSCONFIG=" + mode + "," + now.Groups[1].Value + "," + now.Groups[2].Value + "," + now.Groups[3].Value, 20000);
            }
            else
            {
                r = At.Send("AT+WS46=" + ws46, 20000);
            }
            if (r.Ok)
            {
                string after = FirstMatch(Text(Query("AT+WS46?")), @"\d+");
                if (after != null && after != ws46)
                {
                    r.Ok = false;
                    r.Result = "OK, but the modem still reports its previous setting.";
                }
            }
            return r;
        }

        public AtResponse RegisterAutomatic()
        {
            cancelRequested = false;
            AtResponse r = At.Send("AT+COPS=0", RegisterTimeoutMs);
            if (r.TimedOut && AbortsOnInput)
            {
                At.Interrupt();
                At.WaitIdle(6000);
            }
            if (At.PendingCommand == null) Query("AT+COPS=3,0");
            InvalidateSlowItems();
            return r;
        }

        /// <summary>
        /// Cancels the search or registration that is running (see <see cref="CanCancel"/>). May be called
        /// from another thread while that command is waiting: Qualcomm based modems drop it on any input.
        /// </summary>
        public void Cancel()
        {
            if (!CanCancel) return;
            cancelRequested = true;
            At.Interrupt();
        }

        /// <summary>
        /// Restarts the modem, the software equivalent of unplugging it: it drops off USB and comes back
        /// after a few seconds. This is the way out when a port has stopped answering for good, which the
        /// Nokia 7M-01 firmware can do when commands collide during a registration. The command is sent on
        /// this port if it still answers, otherwise on another AT port of the same device.
        /// Returns null if the restart was started, otherwise the reason it could not be.
        /// </summary>
        public string Restart()
        {
            // The W1208 acknowledges the standard AT+CFUN=1,1 without restarting; Qualcomm's own mode 6 does it.
            string command = Kind == ModemKind.Icera ? "AT%IRESET" : Kind == ModemKind.Sierra ? "AT!RESET"
                : Kind == ModemKind.Qualcomm ? "AT+CFUN=6" : "AT+CFUN=1,1";
            if (CanCancel) At.Interrupt(); // lets a running search or registration go first

            string refusal = null;
            try
            {
                AtResponse r = At.TrySend(command, 4000, 2500);
                if (r != null && !r.Busy)
                {
                    if (r.Ok || r.TimedOut) return null; // accepted, or gone before it could answer
                    refusal = r.Result;
                }
            }
            catch (Exception)
            {
                return null; // the port died under the command: the modem is restarting
            }

            foreach (PortInfo sibling in ModemPorts.Siblings(At.PortName))
            {
                using (AtChannel helper = new AtChannel())
                {
                    try { helper.Open(sibling.Port); }
                    catch (Exception) { continue; }
                    try
                    {
                        AtResponse r = helper.Send(command, 4000, true);
                        ReportHelper(r, sibling.Port);
                        if (r.Ok || r.TimedOut) return null;
                        refusal = r.Result;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
            return "No port of the modem accepted the restart command" + (refusal != null ? " (" + refusal + ")" : "")
                + ". Unplug the modem and plug it in again.";
        }

        /// <summary>Shows a command sent on the helper port in the AT console.</summary>
        private void ReportHelper(AtResponse r, string port)
        {
            r.Command = "[" + port + "] " + r.Command;
            At.Report(r, false);
        }

        // ============================================================ APN profiles

        /// <summary>Profile numbers the modem accepts for AT+CGDCONT (empty if it has no stored profiles).</summary>
        public List<int> ApnIds = new List<int>();
        /// <summary>IP types the modem accepts: IP, IPV6, IPV4V6.</summary>
        public List<string> ApnTypes = new List<string>();
        /// <summary>The modem keeps authentication data per profile (Qualcomm's $QCPDPP).</summary>
        public bool ApnAuth;

        public bool SupportsApnProfiles { get { return ApnTypes.Count > 0; } }

        public List<ApnProfile> ReadApnProfiles()
        {
            AtResponse defined = At.Send("AT+CGDCONT?", 5000);
            if (!defined.Ok) throw new InvalidOperationException("The modem did not list its profiles: " + defined.Result);
            string active = Text(Query("AT+CGACT?"));
            string auth = ApnAuth ? Text(Query("AT$QCPDPP?")) : "";
            return Parsers.ParseApnProfiles(defined.Text, active, auth);
        }

        /// <summary>
        /// Writes a profile. Authentication is only touched when <paramref name="setAuth"/> is true,
        /// because the modem never reports the stored password and it would otherwise be lost.
        /// </summary>
        public AtResponse SaveApnProfile(ApnProfile p, bool setAuth, string password)
        {
            AtResponse r = At.Send("AT+CGDCONT=" + p.Id.ToString(CultureInfo.InvariantCulture)
                + ",\"" + p.Type + "\",\"" + p.Apn + "\"", 15000);
            if (r.Ok && setAuth && ApnAuth)
            {
                // $QCPDPP=<profile>,<auth type>[,<password>,<user name>]
                string head = "AT$QCPDPP=" + p.Id.ToString(CultureInfo.InvariantCulture) + "," + p.Auth.ToString(CultureInfo.InvariantCulture);
                r = p.Auth == 0
                    ? At.Send(head, 15000)
                    : At.Send(head + ",\"" + password + "\",\"" + p.User + "\"", 15000, false, head + ",\"***\",\"" + p.User + "\"");
            }
            InvalidateSlowItems();
            return r;
        }

        public AtResponse DeleteApnProfile(int id)
        {
            AtResponse r = At.Send("AT+CGDCONT=" + id.ToString(CultureInfo.InvariantCulture), 15000);
            InvalidateSlowItems();
            return r;
        }

        // ============================================================ preferences

        public AtResponse SetRatPreference(string index)
        {
            AtResponse r;
            if (Kind == ModemKind.Qualcomm)
            {
                r = SetQualcommType(index);
            }
            else if (Kind == ModemKind.Icera)
            {
                // Keep the service domain (second value) as it is; only the network type changes.
                r = At.Send("AT%IPSYS=" + index + (iceraDomain != null ? "," + iceraDomain : ""), 20000);
                if (r.Ok)
                {
                    // The Nokia 7M-01 answers OK to "prefer 2G" but keeps its old setting, so read it back.
                    Match m = Regex.Match(Text(Query("AT%IPSYS?")), @"%IPSYS:\s*(\d+)");
                    if (m.Success && m.Groups[1].Value != index)
                    {
                        r.Ok = false;
                        r.Result = "OK, but the modem still reports its previous setting. This firmware does not seem to support the chosen mode.";
                    }
                }
            }
            else
            {
                UnlockSierra();
                r = At.Send("AT!SELRAT=" + index, 15000);
            }
            InvalidateSlowItems();
            return r;
        }

        /// <summary>Selects one of the band presets of a Sierra modem.</summary>
        public AtResponse SetBandPreference(string index)
        {
            UnlockSierra();
            AtResponse r = At.Send("AT!BAND=" + index, 15000);
            InvalidateSlowItems();
            return r;
        }

        /// <summary>
        /// Switches bands on a Longcheer based Qualcomm modem: the 2G/3G mask with +BNDPRF (effective at
        /// once), the LTE mask by writing NV item 6828 (effective after a restart). Both are read back.
        /// </summary>
        private AtResponse SetQualcommBands(IList<BandToggle> wanted)
        {
            ulong gw = gwMask, lte = lteMask;
            foreach (BandToggle w in wanted)
            {
                bool isLte = w.Id.StartsWith("LTE", StringComparison.Ordinal);
                int n;
                if (!int.TryParse(w.Id.Substring(isLte ? 3 : 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                int bit = isLte ? n - 1 : n;
                if (bit < 0 || bit > 63) continue;
                if (isLte) lte = w.Enabled ? lte | (1UL << bit) : lte & ~(1UL << bit);
                else gw = w.Enabled ? gw | (1UL << bit) : gw & ~(1UL << bit);
            }

            AtResponse r = null;
            if (gw == 0 || lte == 0)
            {
                // An empty mask is not a state worth trying on real hardware.
                r = new AtResponse();
                r.Command = "band selection";
                r.Result = "Keep at least one 2G/3G band and one LTE band switched on. To use only one network type, "
                    + "set the network type preference instead.";
                return r;
            }

            if (gw != gwMask)
            {
                r = At.Send("AT+BNDPRF=" + Parsers.FormatBndPrf(gw), 15000);
                if (!r.Ok) return r;
                ulong now;
                if (Parsers.ParseBndPrf(Text(Query("AT+BNDPRF?")), out now) && now != gw)
                {
                    r.Ok = false;
                    r.Result = "OK, but the modem reports different 2G/3G bands than requested (" + Parsers.FormatBndPrf(now) + ").";
                    return r;
                }
                gwMask = gw;
            }
            if (lte != lteMask)
            {
                r = At.Send("AT+NVRW=1,6828,\"" + Parsers.FormatNvUInt64(lte) + "\"", 15000);
                if (!r.Ok) return r;
                ulong now;
                if (Parsers.ParseNvUInt64(Text(Query("AT+NVRW=0,6828")), out now) && now != lte)
                {
                    r.Ok = false;
                    r.Result = "OK, but the modem's LTE band setting reads back differently (" + Parsers.FormatNvUInt64(now) + ").";
                    return r;
                }
                lteMask = lte;
                BandChangeNeedsRestart = true;
            }
            return r;
        }

        /// <summary>
        /// Switches individual bands. On an Icera modem with AT%IPBM="name",0|1, where bands are switched
        /// on before others are switched off so that the modem is never left without any band.
        /// Returns the response of the first command that failed, or of the last one sent (null if nothing changed).
        /// </summary>
        public AtResponse SetBands(IList<BandToggle> current, IList<BandToggle> wanted)
        {
            BandChangeNeedsRestart = false;
            if (Kind == ModemKind.Qualcomm)
            {
                AtResponse result = SetQualcommBands(wanted);
                InvalidateSlowItems();
                return result;
            }

            AtResponse lastResponse = null;
            foreach (bool enable in new[] { true, false })
            {
                foreach (BandToggle w in wanted)
                {
                    if (w.Enabled != enable) continue;
                    bool isNow = false;
                    foreach (BandToggle c in current) if (c.Id == w.Id) isNow = c.Enabled;
                    if (isNow == w.Enabled) continue;
                    lastResponse = At.Send("AT%IPBM=\"" + w.Id + "\"," + (enable ? "1" : "0"), 15000);
                    if (!lastResponse.Ok)
                    {
                        InvalidateSlowItems();
                        return lastResponse;
                    }
                }
            }
            InvalidateSlowItems();
            return lastResponse;
        }

        /// <summary>Sierra's extended commands need the (publicly documented) OEM password for write access.</summary>
        private void UnlockSierra()
        {
            Query("AT!ENTERCND=\"A710\"");
        }
    }
}
