using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace SWTool
{
    /// <summary>Result of one AT command.</summary>
    public sealed class AtResponse
    {
        public string Command = "";
        /// <summary>Response payload without the final result code, lines separated by '\n'.</summary>
        public string Text = "";
        /// <summary>Final result code: OK, ERROR, +CME ERROR: ..., or TIMEOUT.</summary>
        public string Result = "";
        public bool Ok;
        public bool TimedOut;
        public int ElapsedMs;
    }

    /// <summary>
    /// Serialised access to the modem's AT command port. All commands are synchronous and
    /// guarded by one lock, so the poller, long running scans and the console cannot interleave.
    /// </summary>
    public sealed class AtChannel : IDisposable
    {
        private readonly object sync = new object();
        private SerialPort port;
        private volatile bool abort;

        public string PortName { get; private set; }

        public bool IsOpen
        {
            get { SerialPort p = port; return p != null && p.IsOpen; }
        }

        /// <summary>Raised (on the calling worker thread) after every command. The flag is true for background polling.</summary>
        public event Action<AtResponse, bool> Traffic;

        public void Open(string portName)
        {
            Close();
            lock (sync)
            {
                abort = false;
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
            if (!r.Ok) r = Send("ATE0", 1500, true);
            if (!r.Ok)
            {
                Close();
                throw new IOException("The device on " + portName + " does not answer AT commands.");
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
            lock (sync)
            {
                SerialPort p = port;
                if (p == null || !p.IsOpen) throw new InvalidOperationException("The port is not open.");

                Stopwatch sw = Stopwatch.StartNew();
                p.DiscardInBuffer();
                p.Write(command + "\r");

                StringBuilder sb = new StringBuilder();
                while (true)
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
                        break;
                    }
                    else
                    {
                        Thread.Sleep(15);
                    }
                }
                resp.ElapsedMs = (int)sw.ElapsedMilliseconds;
            }

            Action<AtResponse, bool> handler = Traffic;
            if (handler != null)
            {
                try { handler(resp, background); } catch { }
            }
            return resp;
        }

        /// <summary>Checks whether the buffer ends with a final result code line.</summary>
        private static bool TryFinish(StringBuilder sb, AtResponse resp)
        {
            char last = sb[sb.Length - 1];
            if (last != '\n' && last != '\r') return false;

            string t = sb.ToString().TrimEnd('\r', '\n', ' ');
            int i = t.LastIndexOfAny(new[] { '\r', '\n' });
            string lastLine = t.Substring(i + 1).Trim();

            bool ok = lastLine == "OK";
            bool err = lastLine == "ERROR" || lastLine == "NO CARRIER"
                || lastLine.StartsWith("+CME ERROR", StringComparison.Ordinal)
                || lastLine.StartsWith("+CMS ERROR", StringComparison.Ordinal);
            if (!ok && !err) return false;

            resp.Ok = ok;
            resp.Result = lastLine;
            resp.Text = Normalize(t.Substring(0, i + 1), resp.Command);
            return true;
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
