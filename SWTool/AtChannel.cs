using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SWTool
{
    /// <summary>Result of one AT command.</summary>
    public sealed class AtResponse
    {
        public string Command = "";
        /// <summary>Response payload without the final result code, lines separated by '\n'.</summary>
        public string Text = "";
        /// <summary>Final result code: OK, ERROR, +CME ERROR: ..., TIMEOUT or BUSY.</summary>
        public string Result = "";
        public bool Ok;
        public bool TimedOut;
        /// <summary>The command was not sent because the modem is still working on an earlier one.</summary>
        public bool Busy;
        public int ElapsedMs;
    }

    /// <summary>
    /// Serialised access to the modem's AT command port. All commands are synchronous and
    /// guarded by one lock, so the poller, long running scans and the console cannot interleave.
    /// </summary>
    /// <remarks>
    /// A command that gets no answer in time stays "pending": some modems (Icera) block the port until
    /// a registration or search is done and cannot be interrupted on that port. While a command is
    /// pending nothing else is sent, because every further command would only queue up behind it. The
    /// channel frees itself as soon as the late result code arrives.
    /// </remarks>
    public sealed class AtChannel : IDisposable
    {
        private readonly object sync = new object();
        private SerialPort port;
        private volatile bool abort;

        private volatile string pendingCommand;
        private readonly Stopwatch pendingClock = new Stopwatch();
        private readonly StringBuilder pendingText = new StringBuilder();
        private long lastProbeMs;
        private long pendingOffsetMs; // time the command had already run when its timeout expired

        // An unsolicited report such as "+CREG: 5,..." or "%NWSTATE: ...": prefix, name, colon.
        private static readonly Regex UnsolicitedLine = new Regex(@"^[+%*^!$][A-Za-z0-9_]+ ?:");

        public string PortName { get; private set; }

        public bool IsOpen
        {
            get { SerialPort p = port; return p != null && p.IsOpen; }
        }

        /// <summary>The command the modem has not answered yet, or null if the port is free.</summary>
        public string PendingCommand { get { return pendingCommand; } }

        /// <summary>How long the pending command has been running, counted from when it was sent.</summary>
        public int PendingSeconds { get { return (int)((pendingOffsetMs + pendingClock.ElapsedMilliseconds) / 1000); } }

        /// <summary>Raised (on the calling worker thread) after every command. The flag is true for background polling.</summary>
        public event Action<AtResponse, bool> Traffic;

        public void Open(string portName)
        {
            Close();
            lock (sync)
            {
                abort = false;
                ClearPending();
                SerialPort p = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One);
                p.Handshake = Handshake.None;
                p.DtrEnable = true;
                p.RtsEnable = true;
                p.ReadTimeout = 500;
                p.WriteTimeout = 2000;
                p.Encoding = Encoding.GetEncoding(28591);
                p.Open();
                port = p;
                PortName = portName;
            }

            // Echo off, verbose errors. The first command may be answered late if the port was idle.
            AtResponse r = Send("ATE0", 1500, true);
            if (!r.Ok)
            {
                lock (sync) ClearPending();
                r = Send("ATE0", 1500, true);
            }
            if (!r.Ok)
            {
                Close();
                throw new IOException("The device on " + portName + " does not answer AT commands. "
                    + "If it is a modem, it may still be busy with an earlier command.");
            }
            Send("AT+CMEE=2", 1500, true);
        }

        public void Close()
        {
            abort = true; // makes a command in flight give up within a few milliseconds
            lock (sync)
            {
                SerialPort p = port;
                port = null;
                ClearPending();
                if (p != null)
                {
                    try { p.Close(); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            Close();
        }

        public AtResponse Send(string command, int timeoutMs = 3000, bool background = false)
        {
            AtResponse resp = new AtResponse();
            resp.Command = command;
            AtResponse late = null;
            lock (sync)
            {
                SerialPort p = port;
                if (p == null || !p.IsOpen) throw new InvalidOperationException("The port is not open.");

                if (pendingCommand != null)
                {
                    late = PendingFinished(p);
                    if (late == null)
                    {
                        resp.TimedOut = true;
                        resp.Busy = true;
                        resp.Result = "BUSY";
                        resp.Text = pendingCommand ?? "";
                    }
                }

                Stopwatch sw = Stopwatch.StartNew();
                StringBuilder sb = new StringBuilder();
                if (!resp.Busy)
                {
                    p.DiscardInBuffer();
                    p.Write(command + "\r");
                }
                while (!resp.Busy)
                {
                    if (abort) throw new OperationCanceledException();
                    string chunk = p.ReadExisting();
                    if (chunk.Length > 0)
                    {
                        sb.Append(chunk);
                        if (TryFinish(sb, resp)) break;
                    }
                    else if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        resp.TimedOut = true;
                        resp.Result = "TIMEOUT";
                        resp.Text = Normalize(sb.ToString(), command);
                        // Keep what arrived so far: the result code may follow much later.
                        pendingCommand = command;
                        pendingOffsetMs = sw.ElapsedMilliseconds;
                        pendingClock.Restart();
                        pendingText.Clear();
                        pendingText.Append(sb);
                        lastProbeMs = 0;
                        break;
                    }
                    else
                    {
                        Thread.Sleep(15);
                    }
                }
                resp.ElapsedMs = (int)sw.ElapsedMilliseconds;
            }

            if (late != null) Report(late, false);
            // Commands that were not sent are only worth showing when somebody asked for them.
            if (!resp.Busy || !background) Report(resp, background);
            return resp;
        }

        /// <summary>
        /// Like <see cref="Send"/>, but gives up if another command holds the port for longer than
        /// <paramref name="lockWaitMs"/>. Returns null in that case, and a Busy response if the modem
        /// itself has not finished an earlier command.
        /// </summary>
        public AtResponse TrySend(string command, int timeoutMs, int lockWaitMs)
        {
            if (!Monitor.TryEnter(sync, lockWaitMs)) return null;
            try { return Send(command, timeoutMs, false); }
            finally { Monitor.Exit(sync); }
        }

        /// <summary>
        /// Waits until the pending command has been answered. Returns true if the port is free.
        /// </summary>
        public bool WaitIdle(int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                AtResponse late = null;
                lock (sync)
                {
                    SerialPort p = port;
                    if (p == null || !p.IsOpen || pendingCommand == null) return pendingCommand == null;
                    late = PendingFinished(p);
                }
                if (late != null)
                {
                    Report(late, false);
                    return true;
                }
                if (abort || sw.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(50);
            }
        }

        /// <summary>
        /// Writes a bare "AT" without waiting for the port to be free. Qualcomm based modems (Sierra)
        /// abort a running search or registration on any input; the command in flight then ends normally.
        /// </summary>
        public void Interrupt()
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) return;
            try { p.Write("AT\r"); } catch { }
        }

        /// <summary>Passes a response to the Traffic listeners (also used for commands sent on a helper port).</summary>
        public void Report(AtResponse response, bool background)
        {
            Action<AtResponse, bool> handler = Traffic;
            if (handler == null) return;
            try { handler(response, background); } catch { }
        }

        private void ClearPending()
        {
            pendingCommand = null;
            pendingClock.Reset();
            pendingText.Clear();
            lastProbeMs = 0;
            pendingOffsetMs = 0;
        }

        /// <summary>
        /// Looks for the late result code of the pending command. Returns its response once it has
        /// arrived (the port is then free), or null while the modem is still busy. Call with the lock held.
        /// </summary>
        private AtResponse PendingFinished(SerialPort p)
        {
            pendingText.Append(p.ReadExisting());
            string all = pendingText.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
            int lastBreak = all.LastIndexOf('\n');
            if (lastBreak >= 0)
            {
                string[] lines = all.Substring(0, lastBreak).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (!IsFinalResult(line)) continue;

                    AtResponse late = new AtResponse();
                    late.Command = pendingCommand;
                    late.Result = line;
                    late.Ok = line == "OK";
                    late.Text = Normalize(string.Join("\n", lines, 0, i), pendingCommand);
                    late.ElapsedMs = (int)(pendingOffsetMs + pendingClock.ElapsedMilliseconds);
                    ClearPending();
                    // Answers to our own probes follow in the same burst; let them arrive and drop them.
                    Thread.Sleep(200);
                    p.DiscardInBuffer();
                    return late;
                }
            }
            if (pendingText.Length > 8000) pendingText.Remove(0, pendingText.Length - 2000);

            // A lost answer on an idle modem would otherwise look like "busy" forever, so ask for a sign
            // of life once a minute. An Icera modem answers the probe when it is done; a Qualcomm modem
            // takes any input as "stop", which ends a registration it would otherwise never give up on.
            // The interval is long enough for a network search to finish undisturbed.
            long ms = pendingClock.ElapsedMilliseconds;
            if (ms - lastProbeMs >= 60000)
            {
                lastProbeMs = ms;
                try { p.Write("AT\r"); } catch (TimeoutException) { }
            }
            return null;
        }

        /// <summary>
        /// Checks whether the buffer ends with a final result code line. Unsolicited reports that arrive
        /// right behind the result code (registration or network state changes) are tolerated and dropped.
        /// </summary>
        private static bool TryFinish(StringBuilder sb, AtResponse resp)
        {
            char last = sb[sb.Length - 1];
            if (last != '\n' && last != '\r') return false;

            string[] lines = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int final = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (IsFinalResult(line)) { final = i; break; }
                if (!UnsolicitedLine.IsMatch(line)) return false; // payload without a result code yet
            }
            if (final < 0) return false;

            resp.Result = lines[final].Trim();
            resp.Ok = resp.Result == "OK";
            resp.Text = Normalize(string.Join("\n", lines, 0, final), resp.Command);
            return true;
        }

        private static bool IsFinalResult(string line)
        {
            return line == "OK" || line == "ERROR" || line == "NO CARRIER"
                || line.StartsWith("+CME ERROR", StringComparison.Ordinal)
                || line.StartsWith("+CMS ERROR", StringComparison.Ordinal);
        }

        private static string Normalize(string s, string command)
        {
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
            // Drop the command echo (only present until ATE0 took effect).
            if (s.StartsWith(command, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(command.Length).TrimStart('\n');
            return s.TrimEnd();
        }
    }
}
