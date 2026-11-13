using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SWTool
{
    /// <summary>High level modem operations on top of the AT channel. All methods block; call them from a worker thread.</summary>
    public sealed class Modem
    {
        public readonly AtChannel At = new AtChannel();
        public DeviceInfo Device = new DeviceInfo();
        public List<Choice> RatChoices = new List<Choice>();
        public List<Choice> BandChoices = new List<Choice>();

        private ModemStatus last;
        private int tick;

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

        private void ReadStatic()
        {
            DeviceInfo d = new DeviceInfo();
            AtResponse r = At.Send("ATI", 3000, true);
            if (r.Ok) Parsers.ParseAti(r.Text, d);

            r = At.Send("AT+CIMI", 3000, true);
            if (r.Ok) d.Imsi = FirstMatch(r.Text, @"\d{10,16}");

            // The command differs between firmware generations ("!ICCID: NOT IMPLEMENTED" still answers OK).
            foreach (string cmd in new[] { "AT+ICCID", "AT!ICCID?", "AT+CCID" })
            {
                r = At.Send(cmd, 3000, true);
                if (r.Ok) d.Iccid = FirstMatch(r.Text, @"\d{18,22}");
                if (d.Iccid != null) break;
            }

            r = At.Send("AT+CNUM", 3000, true);
            if (r.Ok)
            {
                Match m = Regex.Match(r.Text, @"\+CNUM:\s*""[^""]*""\s*,\s*""([^""]+)""");
                if (m.Success) d.OwnNumber = m.Groups[1].Value;
            }
            Device = d;

            r = At.Send("AT!SELRAT=?", 3000, true);
            RatChoices = r.Ok ? Parsers.ParseChoices(r.Text) : new List<Choice>();
            r = At.Send("AT!BAND=?", 3000, true);
            BandChoices = r.Ok ? Parsers.ParseChoices(r.Text) : new List<Choice>();

            At.Send("AT+COPS=3,0", 3000, true); // operator names in long alphanumeric format
        }

        private static string FirstMatch(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern);
            return m.Success ? m.Value : null;
        }

        /// <summary>Reads one status snapshot. Slowly changing items are refreshed on every fifth call.</summary>
        public ModemStatus Poll()
        {
            ModemStatus s = new ModemStatus();
            bool slow = last == null || tick % 5 == 0;
            tick++;

            AtResponse r = At.Send("AT!GSTATUS?", 3000, true);
            if (r.TimedOut)
            {
                s.Responding = false;
                return s;
            }
            if (r.Ok)
            {
                s.HasGStatus = true;
                Parsers.ParseGStatus(r.Text, s);
            }

            if (!s.HasGStatus || s.IsLte)
            {
                r = At.Send("AT!LTEINFO?", 3000, true);
                if (r.Ok) Parsers.ParseLteInfo(r.Text, s);
            }

            r = At.Send("AT+CSQ", 3000, true);
            if (r.Ok) Parsers.ParseCsq(r.Text, s);
            r = At.Send("AT+CESQ", 3000, true);
            if (r.Ok) Parsers.ParseCesq(r.Text, s);

            r = At.Send("AT+COPS?", 3000, true);
            if (r.Ok && Parsers.ParseCops(r.Text, s) == 2)
                At.Send("AT+COPS=3,0", 3000, true); // format was left numeric (e.g. by a manual registration)

            r = At.Send("AT+CREG?", 3000, true);
            if (r.Ok) s.RegCs = Parsers.ParseReg(r.Text);
            r = At.Send("AT+CGREG?", 3000, true);
            if (r.Ok) s.RegPs = Parsers.ParseReg(r.Text);
            r = At.Send("AT+CEREG?", 3000, true);
            if (r.Ok) s.RegEps = Parsers.ParseReg(r.Text);

            if (slow)
            {
                if (s.Plmn == null)
                {
                    At.Send("AT+COPS=3,2", 3000, true);
                    r = At.Send("AT+COPS?", 3000, true);
                    if (r.Ok)
                    {
                        ModemStatus tmp = new ModemStatus();
                        Parsers.ParseCops(r.Text, tmp);
                        s.Plmn = tmp.Plmn;
                    }
                    At.Send("AT+COPS=3,0", 3000, true);
                }

                r = At.Send("AT!SELRAT?", 3000, true);
                if (r.Ok)
                {
                    Match m = Regex.Match(r.Text, @"!SELRAT:\s*([0-9A-Fa-f]+)\s*,\s*(.+)");
                    if (m.Success)
                    {
                        s.RatPrefIndex = m.Groups[1].Value.ToUpperInvariant().PadLeft(2, '0');
                        s.RatPrefName = m.Groups[2].Value.Trim();
                    }
                }

                r = At.Send("AT!BAND?", 3000, true);
                if (r.Ok)
                {
                    List<Choice> current = Parsers.ParseChoices(r.Text);
                    if (current.Count > 0)
                    {
                        s.BandPrefIndex = current[0].Index;
                        s.BandPrefName = current[0].Name;
                    }
                }

                r = At.Send("AT+CGCONTRDP", 3000, true);
                if (r.Ok) Parsers.ParseContext(r.Text, s);
            }
            else
            {
                if (s.Plmn == null) s.Plmn = last.Plmn;
                s.RatPrefIndex = last.RatPrefIndex;
                s.RatPrefName = last.RatPrefName;
                s.BandPrefIndex = last.BandPrefIndex;
                s.BandPrefName = last.BandPrefName;
                s.Apn = last.Apn;
                s.Ipv4 = last.Ipv4;
                s.Ipv6 = last.Ipv6;
            }

            if (string.IsNullOrEmpty(s.Plmn) && s.Mcc != null && s.Mnc != null) s.Plmn = s.Mcc + s.Mnc;

            last = s;
            return s;
        }

        /// <summary>Makes the next poll refresh the slowly changing items too.</summary>
        public void InvalidateSlowItems()
        {
            tick = 0;
        }

        /// <summary>Searches for available networks (AT+COPS=?). Takes 30 s to 3 min.</summary>
        public List<NetworkEntry> Scan(out AtResponse response)
        {
            response = At.Send("AT+COPS=?", 240000);
            return response.Ok ? Parsers.ParseScan(response.Text) : new List<NetworkEntry>();
        }

        /// <summary>Manual registration on a PLMN, optionally forcing the access technology.</summary>
        public AtResponse Register(string plmn, int act)
        {
            // Send the canonical access technology (HSPA variants are selected as UTRAN).
            int a = act == 7 ? 7 : (act == 2 || (act >= 4 && act <= 6)) ? 2 : act >= 0 ? 0 : -1;
            string cmd = "AT+COPS=1,2,\"" + plmn + "\"" + (a >= 0 ? "," + a : "");
            AtResponse r = At.Send(cmd, 150000);
            At.Send("AT+COPS=3,0", 3000, true);
            InvalidateSlowItems();
            return r;
        }

        public AtResponse RegisterAutomatic()
        {
            AtResponse r = At.Send("AT+COPS=0", 150000);
            At.Send("AT+COPS=3,0", 3000, true);
            InvalidateSlowItems();
            return r;
        }

        public AtResponse SetRatPreference(string index)
        {
            Unlock();
            AtResponse r = At.Send("AT!SELRAT=" + index, 15000);
            InvalidateSlowItems();
            return r;
        }

        public AtResponse SetBandPreference(string index)
        {
            Unlock();
            AtResponse r = At.Send("AT!BAND=" + index, 15000);
            InvalidateSlowItems();
            return r;
        }

        /// <summary>Sierra's extended commands need the (publicly documented) OEM password for write access.</summary>
        private void Unlock()
        {
            At.Send("AT!ENTERCND=\"A710\"", 3000, true);
        }
    }
}
