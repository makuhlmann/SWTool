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
                // Location fields (LAC, cell ID, technology) in the registration answers.
                Query("AT+CREG=2");
                Query("AT+CGREG=2");
            }

            if (Kind == ModemKind.Icera) ReadIceraStatic();

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
            if (Kind == ModemKind.Sierra) s.RegEps = Parsers.ParseReg(Text(Query("AT+CEREG?")));

            if (Kind != ModemKind.Sierra) ApplyLocation(s, cs, ps);
            if (Kind == ModemKind.Icera) PollIceraCells(s);

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
        private static void ApplyLocation(ModemStatus s, RegInfo cs, RegInfo ps)
        {
            RegInfo loc = ps.Ci.HasValue ? ps : cs;
            if (loc.Lac.HasValue)
            {
                s.AreaCode = Parsers.HexDec(loc.Lac.Value, 4);
                s.AreaCodeName = "LAC";
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
        /// How long to wait for a registration. A Sierra modem never answers when the network refuses the
        /// SIM, but lets go when interrupted, so it gets a limit. Other modems (seen on the Nokia 7M-01)
        /// answer by themselves after up to about 100 s, block their port until then and cannot be
        /// interrupted on it, so there is no point in giving up early.
        /// </summary>
        private int RegisterTimeoutMs
        {
            get { return Kind == ModemKind.Sierra ? 100000 : 300000; }
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
            if (!res.Success) RestoreSelection(res, plmn, previousMode, previousPlmn);

            if (At.PendingCommand == null) Query("AT+COPS=3,0");
            InvalidateSlowItems();
            return res;
        }

        /// <summary>Puts the network selection back after a registration that failed or was cancelled.</summary>
        private void RestoreSelection(RegisterResult res, string plmn, int previousMode, string previousPlmn)
        {
            if (Kind == ModemKind.Sierra)
            {
                // Any input makes the modem drop the attempt and return to its previous network by itself.
                if (At.PendingCommand != null)
                {
                    At.Interrupt();
                    At.WaitIdle(6000);
                }
                res.Reverted = At.PendingCommand == null && !res.OperatorSelected;
                res.StillPending = At.PendingCommand != null;
                return;
            }

            if (At.PendingCommand != null)
            {
                res.StillPending = true; // nothing can be sent on this port until the modem answers
                return;
            }
            if (res.OperatorSelected) return; // only the type lock failed; the operator selection stands

            // These modems stay in manual mode on the network that refused them, without service.
            AtResponse back = previousMode == 1 && !string.IsNullOrEmpty(previousPlmn) && previousPlmn != plmn
                ? At.Send("AT+COPS=1,2,\"" + previousPlmn + "\"", RegisterTimeoutMs)
                : At.Send("AT+COPS=0", 30000);
            res.Reverted = back.Ok;
        }

        public AtResponse RegisterAutomatic()
        {
            cancelRequested = false;
            AtResponse r = At.Send("AT+COPS=0", RegisterTimeoutMs);
            if (r.TimedOut && Kind == ModemKind.Sierra)
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
            string command = Kind == ModemKind.Icera ? "AT%IRESET" : Kind == ModemKind.Sierra ? "AT!RESET" : "AT+CFUN=1,1";
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

        // ============================================================ preferences

        public AtResponse SetRatPreference(string index)
        {
            AtResponse r;
            if (Kind == ModemKind.Icera)
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
        /// Switches individual bands of an Icera modem (AT%IPBM="name",0|1). Bands are switched on before
        /// others are switched off so that the modem is never left without any band.
        /// Returns the response of the first command that failed, or of the last one sent (null if nothing changed).
        /// </summary>
        public AtResponse SetBands(IList<BandToggle> current, IList<BandToggle> wanted)
        {
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
