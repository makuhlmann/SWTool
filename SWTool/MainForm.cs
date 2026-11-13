using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SWTool
{
    public sealed class MainForm : Form
    {
        private const string SettingsKey = @"Software\SWTool";
        private static readonly int[] Intervals = { 1000, 2000, 5000, 10000, 30000 };

        private readonly Modem modem = new Modem();
        private readonly Timer timer = new Timer();
        private bool connected, polling, busy, userDisconnected, closing, sending;
        private volatile bool logPolling;
        private int intervalMs = 2000, silentPolls;
        private DateTime nextPoll = DateTime.MinValue, nextReconnect = DateTime.MinValue;
        private string currentPort, chartMode, shownRat, shownBand;
        private ModemStatus status;
        private readonly List<string> consoleHistory = new List<string>();
        private int consoleHistoryPos;

        private ComboBox portCombo, intervalCombo, ratCombo, bandCombo;
        private Button connectButton, portsButton, scanButton, registerButton, autoButton, ratApply, bandApply, sendButton;
        private HeaderPanel header;
        private SignalMeter[] meters;
        private Label antennaLabel, scanInfo, selectionLabel;
        private BufferedListView cellList, neighbourList, detailList, networkList;
        private HistoryChart chart;
        private TextBox consoleOut, consoleIn;
        private CheckBox showPolling;
        private ToolStripStatusLabel statusLabel, updatedLabel;

        public MainForm()
        {
            Text = "SWTool - Sierra Wireless modem monitor";
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 770);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = MakeIcon();

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildStatusPage());
            tabs.TabPages.Add(BuildCellsPage());
            tabs.TabPages.Add(BuildNetworksPage());
            tabs.TabPages.Add(BuildDetailsPage());
            tabs.TabPages.Add(BuildConsolePage());

            Panel tabHost = new Panel();
            tabHost.Dock = DockStyle.Fill;
            tabHost.Padding = new Padding(6, 2, 6, 4);
            tabHost.Controls.Add(tabs);

            StatusStrip strip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("Starting...");
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            updatedLabel = new ToolStripStatusLabel("");
            strip.Items.Add(statusLabel);
            strip.Items.Add(updatedLabel);

            Controls.Add(tabHost);
            Controls.Add(BuildToolbar());
            Controls.Add(strip);

            modem.At.Traffic += OnTraffic;
            timer.Interval = 200;
            timer.Tick += OnTimer;

            LoadSettings();
            ShowDisconnected("Not connected");
            UpdateEnabled();
        }

        // ============================================================ UI construction

        private static Icon MakeIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        HeaderPanel.DrawBars(g, new RectangleF(2, 3, 28, 26), 4,
                            Color.FromArgb(0x1F, 0x6F, 0xC4), Color.FromArgb(0xB0, 0xB0, 0xB0));
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { return SystemIcons.Application; }
        }

        private Control BuildToolbar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.AutoSize = true;
            bar.WrapContents = false;
            bar.Padding = new Padding(6, 6, 6, 2);

            portCombo = new ComboBox();
            portCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            portCombo.Width = 450;
            portCombo.Margin = new Padding(3, 4, 3, 3);

            portsButton = MakeButton("Rescan ports", async delegate { await RefreshPortsAsync(false); });
            connectButton = MakeButton("Connect", OnConnectClick);

            intervalCombo = new ComboBox();
            intervalCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            intervalCombo.Width = 62;
            intervalCombo.Margin = new Padding(3, 4, 3, 3);
            foreach (int ms in Intervals) intervalCombo.Items.Add((ms / 1000) + " s");
            intervalCombo.SelectedIndexChanged += delegate
            {
                if (intervalCombo.SelectedIndex < 0) return;
                intervalMs = Intervals[intervalCombo.SelectedIndex];
                nextPoll = DateTime.Now;
                SaveSetting("IntervalMs", intervalMs.ToString(CultureInfo.InvariantCulture));
            };

            bar.Controls.Add(MakeLabel("AT port:"));
            bar.Controls.Add(portCombo);
            bar.Controls.Add(connectButton);
            bar.Controls.Add(portsButton);
            bar.Controls.Add(MakeLabel("    Refresh every:"));
            bar.Controls.Add(intervalCombo);
            return bar;
        }

        private static Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, 8, 3, 3);
            return l;
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(8, 1, 8, 1);
            b.Margin = new Padding(3, 3, 3, 3);
            b.Click += click;
            return b;
        }

        private static BufferedListView MakeList(params string[] columns)
        {
            BufferedListView lv = new BufferedListView();
            lv.Dock = DockStyle.Fill;
            for (int i = 0; i + 1 < columns.Length; i += 2)
                lv.Columns.Add(columns[i], int.Parse(columns[i + 1], CultureInfo.InvariantCulture));
            return lv;
        }

        private TabPage BuildStatusPage()
        {
            TabPage page = new TabPage("Status");
            page.UseVisualStyleBackColor = true;

            header = new HeaderPanel();
            header.Dock = DockStyle.Fill;
            header.Margin = new Padding(0);

            // Signal meters
            GroupBox signalBox = new GroupBox();
            signalBox.Text = "Signal";
            signalBox.Dock = DockStyle.Fill;
            TableLayoutPanel meterTable = new TableLayoutPanel();
            meterTable.Dock = DockStyle.Fill;
            meterTable.ColumnCount = 1;
            meterTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            meterTable.Padding = new Padding(6, 4, 4, 2);
            meters = new SignalMeter[4];
            for (int i = 0; i < meters.Length; i++)
            {
                meters[i] = new SignalMeter();
                meters[i].Dock = DockStyle.Fill;
                meters[i].Margin = new Padding(0);
                meterTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
                meterTable.Controls.Add(meters[i], 0, i);
            }
            antennaLabel = new Label();
            antennaLabel.Dock = DockStyle.Fill;
            antennaLabel.ForeColor = Color.FromArgb(0x55, 0x55, 0x55);
            antennaLabel.Margin = new Padding(0, 8, 0, 0);
            meterTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            meterTable.Controls.Add(antennaLabel, 0, meters.Length);
            signalBox.Controls.Add(meterTable);

            // Cell / connection facts
            GroupBox cellBox = new GroupBox();
            cellBox.Text = "Network and cell";
            cellBox.Dock = DockStyle.Fill;
            cellBox.Padding = new Padding(6, 4, 6, 6);
            cellList = MakeList("Property", "128", "Value", "220");
            cellList.HeaderStyle = ColumnHeaderStyle.None;
            cellList.BorderStyle = BorderStyle.None;
            cellList.FillColumn = 1;
            cellBox.Controls.Add(cellList);

            TableLayoutPanel middle = new TableLayoutPanel();
            middle.Dock = DockStyle.Fill;
            middle.ColumnCount = 2;
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            middle.Margin = new Padding(0);
            middle.Controls.Add(signalBox, 0, 0);
            middle.Controls.Add(cellBox, 1, 0);

            GroupBox chartBox = new GroupBox();
            chartBox.Text = "Signal history (last 10 minutes)";
            chartBox.Dock = DockStyle.Fill;
            chartBox.Padding = new Padding(6, 4, 6, 6);
            chart = new HistoryChart();
            chart.Dock = DockStyle.Fill;
            chartBox.Controls.Add(chart);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 350f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.Padding = new Padding(4);
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(middle, 0, 1);
            root.Controls.Add(chartBox, 0, 2);
            page.Controls.Add(root);
            return page;
        }

        private TabPage BuildCellsPage()
        {
            TabPage page = new TabPage("Cells");
            page.UseVisualStyleBackColor = true;
            page.Padding = new Padding(6);
            neighbourList = MakeList("Cell", "210", "Channel", "80", "Band", "60", "PCI / PSC", "80",
                "RSRP / RSCP (dBm)", "120", "RSRQ / EcN0 (dB)", "120", "RSSI (dBm)", "90");

            Label info = new Label();
            info.Dock = DockStyle.Bottom;
            info.AutoSize = false;
            info.Height = 38;
            info.ForeColor = Color.FromArgb(0x55, 0x55, 0x55);
            info.Text = "The serving cell and the neighbour cells the modem currently measures (AT!LTEINFO). "
                + "Neighbours are only reported while connected to LTE and may be empty when the serving cell is strong.";
            page.Controls.Add(neighbourList);
            page.Controls.Add(info);
            return page;
        }

        private TabPage BuildNetworksPage()
        {
            TabPage page = new TabPage("Networks");
            page.UseVisualStyleBackColor = true;

            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.AutoSize = true;
            top.WrapContents = false;
            scanButton = MakeButton("Search for networks", OnScanClick);
            scanInfo = MakeLabel("A search takes 30 seconds to 2 minutes. Data transfer may pause while it runs; Disconnect aborts it.");
            top.Controls.Add(scanButton);
            top.Controls.Add(scanInfo);

            networkList = MakeList("Operator", "220", "Short name", "130", "PLMN (MCC MNC)", "120",
                "Network type", "110", "Status", "110");
            networkList.MultiSelect = false;
            networkList.ShowGroups = true;
            networkList.SelectedIndexChanged += delegate { UpdateEnabled(); };
            networkList.DoubleClick += OnRegisterClick;

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.WrapContents = false;
            registerButton = MakeButton("Register on selected network", OnRegisterClick);
            autoButton = MakeButton("Automatic selection", OnAutomaticClick);
            selectionLabel = MakeLabel("");
            actions.Controls.Add(registerButton);
            actions.Controls.Add(autoButton);
            actions.Controls.Add(selectionLabel);

            GroupBox prefs = new GroupBox();
            prefs.Text = "Modem preferences (stored in the modem)";
            prefs.Dock = DockStyle.Fill;
            prefs.AutoSize = true;
            FlowLayoutPanel prefFlow = new FlowLayoutPanel();
            prefFlow.Dock = DockStyle.Fill;
            prefFlow.AutoSize = true;
            prefFlow.WrapContents = true;
            prefFlow.Padding = new Padding(2, 2, 2, 4);
            ratCombo = new ComboBox();
            ratCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            ratCombo.Width = 170;
            ratCombo.Margin = new Padding(3, 4, 3, 3);
            ratCombo.SelectedIndexChanged += delegate { UpdateEnabled(); };
            ratApply = MakeButton("Apply", OnRatApplyClick);
            bandCombo = new ComboBox();
            bandCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            bandCombo.Width = 170;
            bandCombo.Margin = new Padding(3, 4, 3, 3);
            bandCombo.SelectedIndexChanged += delegate { UpdateEnabled(); };
            bandApply = MakeButton("Apply", OnBandApplyClick);
            prefFlow.Controls.Add(MakeLabel("Network type:"));
            prefFlow.Controls.Add(ratCombo);
            prefFlow.Controls.Add(ratApply);
            prefFlow.Controls.Add(MakeLabel("      Bands:"));
            prefFlow.Controls.Add(bandCombo);
            prefFlow.Controls.Add(bandApply);
            prefs.Controls.Add(prefFlow);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Padding = new Padding(4);
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(networkList, 0, 1);
            root.Controls.Add(actions, 0, 2);
            root.Controls.Add(prefs, 0, 3);
            page.Controls.Add(root);
            return page;
        }

        private TabPage BuildDetailsPage()
        {
            TabPage page = new TabPage("Details");
            page.UseVisualStyleBackColor = true;
            page.Padding = new Padding(6);
            detailList = MakeList("Property", "220", "Value", "400");
            detailList.ShowGroups = true;
            detailList.FillColumn = 1;
            page.Controls.Add(detailList);
            return page;
        }

        private TabPage BuildConsolePage()
        {
            TabPage page = new TabPage("AT console");
            page.UseVisualStyleBackColor = true;

            consoleOut = new TextBox();
            consoleOut.Multiline = true;
            consoleOut.ReadOnly = true;
            consoleOut.ScrollBars = ScrollBars.Both;
            consoleOut.WordWrap = false;
            consoleOut.BackColor = Color.White;
            consoleOut.Font = new Font("Consolas", 9.5f);
            consoleOut.Dock = DockStyle.Fill;

            consoleIn = new TextBox();
            consoleIn.Font = new Font("Consolas", 9.5f);
            consoleIn.Dock = DockStyle.Fill;
            consoleIn.Margin = new Padding(3, 5, 3, 3);
            consoleIn.KeyDown += OnConsoleKeyDown;

            sendButton = MakeButton("Send", delegate { SendConsoleCommand(); });
            showPolling = new CheckBox();
            showPolling.Text = "Show background polling";
            showPolling.AutoSize = true;
            showPolling.Margin = new Padding(8, 7, 3, 3);
            showPolling.CheckedChanged += delegate { logPolling = showPolling.Checked; };
            Button clear = MakeButton("Clear", delegate { consoleOut.Clear(); });

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.AutoSize = true;
            bottom.ColumnCount = 4;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.Controls.Add(consoleIn, 0, 0);
            bottom.Controls.Add(sendButton, 1, 0);
            bottom.Controls.Add(clear, 2, 0);
            bottom.Controls.Add(showPolling, 3, 0);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Padding = new Padding(4);
            root.Controls.Add(consoleOut, 0, 0);
            root.Controls.Add(bottom, 0, 1);
            page.Controls.Add(root);
            return page;
        }

        // ============================================================ settings

        private void LoadSettings()
        {
            int ms = 2000;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    if (k != null)
                    {
                        currentPort = k.GetValue("Port") as string;
                        int.TryParse(k.GetValue("IntervalMs") as string, out ms);
                    }
                }
            }
            catch { }
            int idx = Array.IndexOf(Intervals, ms);
            intervalCombo.SelectedIndex = idx >= 0 ? idx : 1;
        }

        private static void SaveSetting(string name, string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (k != null) k.SetValue(name, value ?? "");
                }
            }
            catch { }
        }

        // ============================================================ connection handling

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await RefreshPortsAsync(true);
            timer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            timer.Stop();
            modem.At.Traffic -= OnTraffic;
            modem.Close();
            base.OnFormClosing(e);
        }

        private async Task RefreshPortsAsync(bool autoConnect)
        {
            portsButton.Enabled = false;
            if (!connected) SetStatus("Looking for modem ports...");
            List<PortInfo> ports;
            try { ports = await Task.Run(() => ModemPorts.Detect()); }
            catch { ports = new List<PortInfo>(); }
            if (closing) return;

            string wanted = connected ? currentPort : (SelectedPort() ?? currentPort);
            portCombo.Items.Clear();
            int select = -1;
            for (int i = 0; i < ports.Count; i++)
            {
                portCombo.Items.Add(ports[i]);
                if (ports[i].Port == wanted) select = i;
            }
            // Without a remembered port, take the best candidate (a Sierra modem port sorts first).
            if (select < 0 && ports.Count > 0 && ports[0].Rank >= 40) select = 0;
            if (select < 0 && ports.Count > 0 && !autoConnect) select = 0;
            portCombo.SelectedIndex = select;
            portsButton.Enabled = true;

            if (connected) return;
            if (ports.Count == 0) SetStatus("No serial ports found. Is the modem's AT port enabled and its driver installed?");
            else if (select < 0) SetStatus("No modem port detected automatically. Choose the AT port and press Connect.");
            else if (autoConnect) await ConnectAsync(((PortInfo)portCombo.SelectedItem).Port);
            else SetStatus("Not connected");
        }

        private string SelectedPort()
        {
            PortInfo p = portCombo.SelectedItem as PortInfo;
            return p != null ? p.Port : null;
        }

        /// <summary>True while a lost or failed connection is being retried automatically.</summary>
        private bool Retrying
        {
            get
            {
                return !connected && !userDisconnected && currentPort != null
                    && nextReconnect > DateTime.MinValue && SelectedPort() == currentPort;
            }
        }

        private async void OnConnectClick(object sender, EventArgs e)
        {
            if (connected || Retrying)
            {
                // Also aborts a running network search: closing the port cancels the command in flight.
                userDisconnected = true;
                nextReconnect = DateTime.MinValue;
                Disconnect("Disconnected");
                UpdateEnabled();
                return;
            }
            if (busy) return;
            string port = SelectedPort();
            if (port == null) return;
            userDisconnected = false;
            await ConnectAsync(port);
        }

        private async Task ConnectAsync(string port)
        {
            if (busy || closing) return;
            busy = true;
            UpdateEnabled();
            SetStatus("Connecting to " + port + "...");
            try
            {
                await Task.Run(() => modem.Connect(port));
                if (closing) return;
                connected = true;
                currentPort = port;
                silentPolls = 0;
                shownRat = shownBand = null;
                SaveSetting("Port", port);
                FillChoices();
                header.Set(0, Color.Gray, "Reading modem status...", "", "");
                SetStatus("Connected to " + port + DeviceSuffix());
                nextPoll = DateTime.Now;
            }
            catch (Exception ex)
            {
                if (closing) return;
                connected = false;
                currentPort = port;
                modem.Close();
                ShowDisconnected("Not connected");
                if (userDisconnected)
                {
                    SetStatus("Disconnected");
                }
                else
                {
                    nextReconnect = DateTime.Now.AddSeconds(5);
                    SetStatus("Could not open " + port + ": " + Friendly(ex) + "  (retrying)");
                }
            }
            finally
            {
                busy = false;
                if (!closing) UpdateEnabled();
            }
        }

        private string DeviceSuffix()
        {
            DeviceInfo d = modem.Device;
            return string.IsNullOrEmpty(d.Model) ? "" : "  -  " + d.Model + (string.IsNullOrEmpty(d.Revision) ? "" : ", firmware " + d.Revision.Split(' ')[0]);
        }

        private static string Friendly(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "the port is in use by another program";
            if (ex is AggregateException && ex.InnerException != null) return Friendly(ex.InnerException);
            return ex.Message;
        }

        private void Disconnect(string reason)
        {
            connected = false;
            modem.Close();
            ShowDisconnected(reason);
            SetStatus(reason);
        }

        private void ConnectionLost(Exception ex)
        {
            if (closing || !connected) return;
            Disconnect("Connection to the modem lost");
            SetStatus("Connection lost: " + Friendly(ex) + "  (reconnecting)");
            nextReconnect = DateTime.Now.AddSeconds(3);
            UpdateEnabled();
        }

        private void SetStatus(string text)
        {
            statusLabel.Text = text;
        }

        private void UpdateEnabled()
        {
            bool idle = connected && !busy;
            bool waiting = Retrying;
            connectButton.Text = connected ? "Disconnect" : waiting ? "Stop retrying" : "Connect";
            connectButton.Enabled = connected || (!busy && (waiting || portCombo.SelectedItem != null));
            portCombo.Enabled = !connected && !busy;
            scanButton.Enabled = idle;
            autoButton.Enabled = idle;
            registerButton.Enabled = idle && networkList.SelectedItems.Count == 1;
            ratCombo.Enabled = idle && ratCombo.Items.Count > 0;
            bandCombo.Enabled = idle && bandCombo.Items.Count > 0;
            ratApply.Enabled = idle && ratCombo.SelectedItem != null;
            bandApply.Enabled = idle && bandCombo.SelectedItem != null;
            sendButton.Enabled = connected && !sending;
        }

        private void FillChoices()
        {
            ratCombo.Items.Clear();
            foreach (Choice c in modem.RatChoices) ratCombo.Items.Add(c);
            bandCombo.Items.Clear();
            foreach (Choice c in modem.BandChoices) bandCombo.Items.Add(c);
        }

        // ============================================================ polling

        private async void OnTimer(object sender, EventArgs e)
        {
            if (closing || polling || busy) return;
            DateTime now = DateTime.Now;

            if (!connected)
            {
                if (!userDisconnected && currentPort != null && nextReconnect > DateTime.MinValue && now >= nextReconnect)
                {
                    nextReconnect = now.AddSeconds(5);
                    await ConnectAsync(currentPort);
                }
                return;
            }
            if (now < nextPoll) return;

            polling = true;
            nextPoll = now.AddMilliseconds(intervalMs);
            try
            {
                ModemStatus s = await Task.Run(() => modem.Poll());
                if (closing || !connected) return;
                if (!s.Responding)
                {
                    if (++silentPolls >= 3) ConnectionLost(new TimeoutException("the modem stopped answering"));
                    else SetStatus("The modem is not responding...");
                    return;
                }
                if (silentPolls > 0 || statusLabel.Text.StartsWith("The modem is not", StringComparison.Ordinal))
                    SetStatus("Connected to " + currentPort + DeviceSuffix());
                silentPolls = 0;
                ShowStatus(s);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ConnectionLost(ex);
            }
            finally
            {
                polling = false;
            }
        }

        /// <summary>Runs a long modem operation while the poller is paused and the action buttons are disabled.</summary>
        private async Task<T> RunBusy<T>(string statusText, Func<T> operation)
        {
            string previous = statusLabel.Text;
            busy = true;
            UpdateEnabled();
            SetStatus(statusText);
            UseWaitCursor = true;
            try
            {
                return await Task.Run(operation);
            }
            finally
            {
                UseWaitCursor = false;
                busy = false;
                if (!closing)
                {
                    if (statusLabel.Text == statusText) SetStatus(previous);
                    nextPoll = DateTime.Now;
                    UpdateEnabled();
                }
            }
        }

        // ============================================================ status display

        private void ShowDisconnected(string title)
        {
            status = null;
            chartMode = null;
            header.Set(0, Color.Gray, title, "", "");
            for (int i = 0; i < meters.Length; i++) meters[i].Visible = false;
            antennaLabel.Text = "";
            cellList.Items.Clear();
            neighbourList.Items.Clear();
            detailList.Items.Clear();
            selectionLabel.Text = "";
            updatedLabel.Text = "";
            chart.Clear();
        }

        private void ShowStatus(ModemStatus s)
        {
            status = s;
            CultureInfo inv = CultureInfo.InvariantCulture;
            bool lte = s.IsLte, wcdma = s.IsWcdma;
            bool lowPower = s.ModemMode != null && s.ModemMode.IndexOf("ONLINE", StringComparison.OrdinalIgnoreCase) < 0;

            // ---- header
            string title, line2, line3;
            if (lowPower)
            {
                title = "Radio off";
                line2 = "Modem mode: " + s.ModemMode;
                line3 = "";
            }
            else if (!s.Registered)
            {
                title = s.RegStat == 2 ? "Searching for network..." : s.RegStat == 3 ? "Registration denied" : "No service";
                line2 = s.RegText;
                line3 = s.ServiceState ?? "";
            }
            else
            {
                title = string.IsNullOrEmpty(s.OperatorName) ? Parsers.FormatPlmn(s.Plmn) : s.OperatorName;
                if (title.Length == 0) title = "(unknown operator)";
                line2 = s.NetworkType;
                string band = BandText(s);
                if (band.Length > 0) line2 += "   |   " + band;
                if (!string.IsNullOrEmpty(s.Bandwidth)) line2 += "   |   " + s.Bandwidth;
                line3 = s.Roaming ? "Roaming" : "Home network";
                if (!string.IsNullOrEmpty(s.Plmn)) line3 += "   |   PLMN " + Parsers.FormatPlmn(s.Plmn);
                if (!string.IsNullOrEmpty(s.PsState)) line3 += "   |   Data " + s.PsState.ToLowerInvariant();
                line3 += "   |   " + SelectionText(s);
            }
            int bars = lowPower ? 0 : s.Bars;
            header.Set(bars, Rating.Colour(Rating.FromBars(bars)), title, line2, line3);

            // ---- meters and history
            string mode = lte ? "LTE" : wcdma ? "WCDMA" : "OTHER";
            if (mode != chartMode)
            {
                chartMode = mode;
                if (lte) chart.Configure("RSRP", "dBm", -140, -60, "SINR", "dB", -10, 30);
                else if (wcdma) chart.Configure("RSCP", "dBm", -120, -40, "Ec/Io", "dB", -24, 0);
                else chart.Configure("RSSI", "dBm", -110, -50, null, null, 0, 1);
            }
            if (lte)
            {
                meters[0].Set("RSRP", "Reference signal received power", s.Rsrp, "dBm", -130, -60, Rating.Rsrp(s.Rsrp));
                meters[1].Set("RSRQ", "Reference signal received quality", s.Rsrq, "dB", -20, -3, Rating.Rsrq(s.Rsrq));
                meters[2].Set("SINR", "Signal to interference and noise ratio", s.Sinr, "dB", -10, 30, Rating.Sinr(s.Sinr));
                meters[3].Set("RSSI", "Total received power", s.Rssi, "dBm", -110, -40, Rating.Rssi(s.Rssi));
                SetMeterCount(4);
                chart.Add(s.Time, s.Rsrp, s.Sinr);
            }
            else if (wcdma)
            {
                meters[0].Set("RSCP", "Received signal code power", s.Rscp, "dBm", -120, -50, Rating.Rscp(s.Rscp));
                meters[1].Set("Ec/Io", "Pilot quality", s.Ecio, "dB", -24, 0, Rating.Ecio(s.Ecio));
                meters[2].Set("RSSI", "Total received power", s.Rssi, "dBm", -110, -40, Rating.Rssi(s.Rssi));
                SetMeterCount(3);
                chart.Add(s.Time, s.Rscp, s.Ecio);
            }
            else
            {
                meters[0].Set("RSSI", "Received signal strength", s.Rssi, "dBm", -113, -51, Rating.Rssi(s.Rssi));
                SetMeterCount(1);
                chart.Add(s.Time, s.Rssi, null);
            }

            StringBuilder ant = new StringBuilder();
            foreach (RxPath p in s.Paths)
            {
                if (!p.Rssi.HasValue && !p.Rsrp.HasValue) continue;
                if (ant.Length > 0) ant.AppendLine();
                ant.Append("Antenna ").Append(p.Name).Append(":  RSSI ").Append(Parsers.Fmt(p.Rssi, "dBm"));
                if (p.Rsrp.HasValue) ant.Append(",  RSRP ").Append(Parsers.Fmt(p.Rsrp, "dBm"));
            }
            antennaLabel.Text = ant.ToString();

            // ---- network and cell facts
            List<string[]> rows = new List<string[]>();
            string bandLine = BandText(s);
            if (!string.IsNullOrEmpty(s.Bandwidth)) bandLine += (bandLine.Length > 0 ? ", " : "") + s.Bandwidth + " wide";
            AddRow(rows, "Band", bandLine);
            AddRow(rows, "Channel", ChannelText(s));
            AddRow(rows, "Operator ID (PLMN)", PlmnText(s));
            AddRow(rows, "Registration", s.RegText);
            AddRow(rows, s.AreaCodeName == "LAC" ? "Location area (LAC)" : "Tracking area (TAC)", s.AreaCode);
            AddRow(rows, "Cell ID", s.CellIdText);
            if (s.CellId.HasValue && lte)
                AddRow(rows, "eNodeB / sector", (s.CellId.Value >> 8).ToString(inv) + " / " + (s.CellId.Value & 0xFF).ToString(inv));
            else if (s.CellId.HasValue && wcdma)
                AddRow(rows, "RNC / cell", (s.CellId.Value >> 16).ToString(inv) + " / " + (s.CellId.Value & 0xFFFF).ToString(inv));
            if (s.Pci.HasValue) AddRow(rows, "Physical cell ID", s.Pci.Value.ToString(inv));
            AddRow(rows, "Service state", s.ServiceState);
            AddRow(rows, "RRC state", s.RrcState);
            AddRow(rows, "Carrier aggregation", s.CaState);
            AddRow(rows, "Packet data", s.PsState == null ? null : s.PsState + (string.IsNullOrEmpty(s.Apn) ? "" : "  (APN " + s.Apn + ")"));
            AddRow(rows, "IP address", s.Ipv4);
            AddRow(rows, "IPv6 address", s.Ipv6);
            if (s.TxPower.HasValue) AddRow(rows, "Transmit power", Parsers.Fmt(s.TxPower, "dBm"));
            if (s.Temperature.HasValue) AddRow(rows, "Module temperature", s.Temperature.Value.ToString("0.#", inv) + " °C");
            cellList.SetRows(rows, null);

            // ---- neighbour cells
            List<string[]> cells = new List<string[]>();
            foreach (CellInfo c in s.Cells)
                cells.Add(new[] { c.Kind, c.Channel, c.Band, c.Id, c.Level, c.Qual, c.Rssi });
            neighbourList.SetRows(cells, null);

            ShowDetails(s);

            // ---- networks page
            selectionLabel.Text = "Current: " + SelectionText(s)
                + (string.IsNullOrEmpty(s.OperatorName) ? "" : ", registered on " + s.OperatorName);
            SyncChoice(ratCombo, s.RatPrefIndex, ref shownRat);
            SyncChoice(bandCombo, s.BandPrefIndex, ref shownBand);

            updatedLabel.Text = "Updated " + s.Time.ToString("HH:mm:ss", inv);
        }

        private void SetMeterCount(int n)
        {
            for (int i = 0; i < meters.Length; i++)
                if (meters[i].Visible != (i < n)) meters[i].Visible = i < n;
        }

        private static void AddRow(List<string[]> rows, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) rows.Add(new[] { key, value });
        }

        private static string BandText(ModemStatus s)
        {
            if (string.IsNullOrEmpty(s.Band)) return "";
            if (s.IsLte)
            {
                double? n = Parsers.Num(s.Band);
                string name = n.HasValue ? LteBands.Name((int)n.Value) : null;
                return name != null ? s.Band + " (" + name + ")" : s.Band;
            }
            return s.Band;
        }

        private static string ChannelText(ModemStatus s)
        {
            if (string.IsNullOrEmpty(s.RxChannel)) return null;
            if (!s.IsLte) return (s.IsWcdma ? "UARFCN " : "ARFCN ") + s.RxChannel;
            string text = "EARFCN " + s.RxChannel;
            double? ch = Parsers.Num(s.RxChannel);
            int band; double mhz;
            if (ch.HasValue && LteBands.FromEarfcn((int)ch.Value, out band, out mhz))
                text += "  (downlink " + mhz.ToString("0.0", CultureInfo.InvariantCulture) + " MHz)";
            return text;
        }

        private static string PlmnText(ModemStatus s)
        {
            if (!string.IsNullOrEmpty(s.Plmn)) return Parsers.FormatPlmn(s.Plmn);
            return s.Mcc != null && s.Mnc != null ? s.Mcc + " " + s.Mnc : null;
        }

        private static string SelectionText(ModemStatus s)
        {
            switch (s.CopsMode)
            {
                case 0: return "Automatic selection";
                case 1: return "Manual selection";
                case 2: return "Deregistered";
                case 4: return "Manual selection with automatic fallback";
                default: return "Selection mode unknown";
            }
        }

        private void ShowDetails(ModemStatus s)
        {
            DeviceInfo d = modem.Device;
            List<string[]> rows = new List<string[]>();
            List<string> groups = new List<string>();
            Action<string, string, string> add = delegate(string g, string k, string v)
            {
                if (string.IsNullOrEmpty(v)) return;
                rows.Add(new[] { k, v });
                groups.Add(g);
            };

            const string gd = "Device and SIM", gn = "Network", gs = "Modem status (AT!GSTATUS?)";
            add(gd, "Manufacturer", d.Manufacturer);
            add(gd, "Model", d.Model);
            add(gd, "Firmware", d.Revision);
            add(gd, "IMEI", d.Imei);
            add(gd, "Serial number (FSN)", d.Serial);
            add(gd, "IMSI", d.Imsi);
            add(gd, "SIM card number (ICCID)", d.Iccid);
            add(gd, "Phone number", d.OwnNumber);
            add(gd, "AT port", currentPort);

            add(gn, "Operator", s.OperatorName);
            add(gn, "Operator ID (PLMN)", PlmnText(s));
            add(gn, "Network selection", SelectionText(s));
            add(gn, "Registration (LTE / EPS)", RegName(s.RegEps));
            add(gn, "Registration (packet / GPRS)", RegName(s.RegPs));
            add(gn, "Registration (circuit / voice)", RegName(s.RegCs));
            add(gn, "Network type preference", s.RatPrefName);
            add(gn, "Band preference", s.BandPrefName);
            add(gn, "APN", s.Apn);
            add(gn, "IP address", s.Ipv4);
            add(gn, "IPv6 address", s.Ipv6);
            if (s.Csq.HasValue)
                add(gn, "Signal (AT+CSQ)", s.Csq.Value + " of 31  (" + (-113 + 2 * s.Csq.Value) + " dBm)");
            if (s.Rscp.HasValue && !s.IsLte) add(gn, "RSCP", Parsers.Fmt(s.Rscp, "dBm"));
            if (s.Ecio.HasValue && !s.IsLte) add(gn, "Ec/Io", Parsers.Fmt(s.Ecio, "dB"));

            foreach (KeyValuePair<string, string> kv in s.Details) add(gs, kv.Key, kv.Value);
            detailList.SetRows(rows, groups);
        }

        private static string RegName(int stat)
        {
            switch (stat)
            {
                case 0: return "Not registered";
                case 1: return "Registered, home network";
                case 2: return "Searching";
                case 3: return "Denied";
                case 4: return "Unknown";
                case 5: return "Registered, roaming";
                default: return stat < 0 ? null : stat.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Selects the modem's current setting, but only when it changed, so a pending user choice is kept.</summary>
        private static void SyncChoice(ComboBox combo, string index, ref string shown)
        {
            if (index == null || index == shown) return;
            shown = index;
            foreach (object o in combo.Items)
            {
                if (((Choice)o).Index != index) continue;
                combo.SelectedItem = o;
                return;
            }
        }

        // ============================================================ network search and selection

        private async void OnScanClick(object sender, EventArgs e)
        {
            if (!connected || busy) return;
            scanInfo.Text = "Searching... this can take up to 2 minutes.";
            DateTime start = DateTime.Now;
            AtResponse resp = null;
            List<NetworkEntry> found;
            try
            {
                found = await RunBusy("Searching for networks...", delegate
                {
                    AtResponse r;
                    List<NetworkEntry> list = modem.Scan(out r);
                    resp = r;
                    return list;
                });
            }
            catch (Exception ex)
            {
                if (closing) return;
                scanInfo.Text = ex is OperationCanceledException ? "Search cancelled." : "Search failed.";
                ConnectionLost(ex);
                return;
            }
            if (closing) return;

            if (resp == null || !resp.Ok)
            {
                scanInfo.Text = "Search failed: " + (resp != null ? resp.Result : "no response");
                return;
            }

            ShowNetworks(found);
            int lte = 0, umts = 0, gsm = 0;
            foreach (NetworkEntry n in found)
            {
                if (n.Act == 7) lte++;
                else if (n.Act == 2 || (n.Act >= 4 && n.Act <= 6)) umts++;
                else gsm++;
            }
            scanInfo.Text = string.Format(CultureInfo.InvariantCulture,
                "{0} networks found at {1:HH:mm:ss} in {2:0} s:  {3} LTE,  {4} UMTS,  {5} GSM",
                found.Count, DateTime.Now, (DateTime.Now - start).TotalSeconds, lte, umts, gsm);
        }

        private void ShowNetworks(List<NetworkEntry> found)
        {
            networkList.BeginUpdate();
            networkList.Items.Clear();
            networkList.Groups.Clear();
            string[] order = { "LTE (4G)", "UMTS (3G)", "GSM (2G)", "Unknown" };
            Dictionary<string, ListViewGroup> groups = new Dictionary<string, ListViewGroup>();
            foreach (string name in order)
            {
                bool any = false;
                foreach (NetworkEntry n in found) any |= Parsers.AccessTechnology(n.Act) == name;
                if (!any) continue;
                ListViewGroup g = new ListViewGroup(name);
                groups[name] = g;
                networkList.Groups.Add(g);
            }

            // Current network first, then available, then forbidden ones.
            found.Sort(delegate(NetworkEntry a, NetworkEntry b)
            {
                int c = StatOrder(a.Stat).CompareTo(StatOrder(b.Stat));
                return c != 0 ? c : string.Compare(a.LongName, b.LongName, StringComparison.OrdinalIgnoreCase);
            });
            foreach (NetworkEntry n in found)
            {
                string tech = Parsers.AccessTechnology(n.Act);
                ListViewItem it = new ListViewItem(new[] { n.LongName, n.ShortName, Parsers.FormatPlmn(n.Plmn), tech, n.StatText });
                it.Tag = n;
                it.Group = groups[tech];
                if (n.Stat == 2) it.Font = new Font(networkList.Font, FontStyle.Bold);
                else if (n.Stat == 3) it.ForeColor = Color.FromArgb(0x99, 0x99, 0x99);
                networkList.Items.Add(it);
            }
            networkList.EndUpdate();
            UpdateEnabled();
        }

        private static int StatOrder(int stat)
        {
            return stat == 2 ? 0 : stat == 1 ? 1 : stat == 0 ? 2 : 3;
        }

        private async void OnRegisterClick(object sender, EventArgs e)
        {
            if (!connected || busy || networkList.SelectedItems.Count != 1) return;
            NetworkEntry n = networkList.SelectedItems[0].Tag as NetworkEntry;
            if (n == null) return;
            string tech = Parsers.AccessTechnology(n.Act);
            string what = n.LongName + " (" + Parsers.FormatPlmn(n.Plmn) + ")" + (n.Act >= 0 ? " using " + tech : "");

            string question = "Register manually on " + what + "?\n\n"
                + "The modem stays on this network until you choose another one or switch back to automatic selection.";
            if (n.Act >= 0)
            {
                // Observed on the EM7455: AT+COPS with an access technology also rewrites the !SELRAT preference.
                question += "\n\nThis also changes the modem's network type preference to " + tech + " only. "
                    + "It will not fall back to another network type until you change that preference again "
                    + "(\"Network type\" below).";
            }
            question += "\n\nThe data connection is briefly interrupted and usually comes back with a new IP address.";
            if (n.Stat == 3) question += "\n\nThis network is marked as forbidden for your SIM card, so the registration will probably be rejected.";
            if (MessageBox.Show(this, question, "Change network", MessageBoxButtons.OKCancel,
                n.Stat == 3 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;

            await RunCommand("Registering on " + n.LongName + "...", delegate { return modem.Register(n.Plmn, n.Act); },
                "The modem could not register on " + what + ".");
        }

        private async void OnAutomaticClick(object sender, EventArgs e)
        {
            if (!connected || busy) return;
            if (MessageBox.Show(this, "Switch back to automatic network selection?\n\n"
                + "The modem then chooses the network itself, which may briefly interrupt the data connection.",
                "Automatic selection", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await RunCommand("Switching to automatic network selection...", delegate { return modem.RegisterAutomatic(); },
                "The modem could not switch to automatic network selection.");
        }

        private async void OnRatApplyClick(object sender, EventArgs e)
        {
            Choice c = ratCombo.SelectedItem as Choice;
            if (!connected || busy || c == null) return;
            if (MessageBox.Show(this, "Set the network type preference to \"" + c.Name + "\"?\n\n"
                + "The setting is stored in the modem and survives restarts. If no network of this type is in range, "
                + "the modem has no service until you change it back.",
                "Network type preference", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            shownRat = null;
            await RunCommand("Setting network type preference...", delegate { return modem.SetRatPreference(c.Index); },
                "The modem rejected the network type preference.");
        }

        private async void OnBandApplyClick(object sender, EventArgs e)
        {
            Choice c = bandCombo.SelectedItem as Choice;
            if (!connected || busy || c == null) return;
            if (MessageBox.Show(this, "Set the band preference to \"" + c.Name + "\"?\n\n"
                + "The setting is stored in the modem and survives restarts. If none of these bands is available here, "
                + "the modem has no service until you change it back.",
                "Band preference", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            shownBand = null;
            await RunCommand("Setting band preference...", delegate { return modem.SetBandPreference(c.Index); },
                "The modem rejected the band preference.");
        }

        private async Task RunCommand(string statusText, Func<AtResponse> command, string failureText)
        {
            AtResponse r;
            try
            {
                r = await RunBusy(statusText, command);
            }
            catch (Exception ex)
            {
                if (!closing) ConnectionLost(ex);
                return;
            }
            if (closing) return;
            if (r.Ok)
            {
                SetStatus("Done: " + r.Command);
            }
            else
            {
                string reason = r.TimedOut ? "The modem did not answer in time." : "Modem response: " + r.Result;
                MessageBox.Show(this, failureText + "\n\n" + reason, "SWTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ============================================================ AT console

        private void OnTraffic(AtResponse r, bool background)
        {
            if (closing || (background && !logPolling)) return;
            try
            {
                BeginInvoke((Action)delegate { AppendConsole(r); });
            }
            catch { }
        }

        private void AppendConsole(AtResponse r)
        {
            if (closing) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("> ").AppendLine(r.Command);
            if (r.Text.Length > 0) sb.AppendLine(r.Text.Replace("\n", "\r\n"));
            sb.Append(r.Result).Append("   [").Append(r.ElapsedMs).AppendLine(" ms]");
            sb.AppendLine();
            if (consoleOut.TextLength > 300000) consoleOut.Text = consoleOut.Text.Substring(150000);
            consoleOut.AppendText(sb.ToString());
        }

        private void OnConsoleKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                SendConsoleCommand();
            }
            else if (e.KeyCode == Keys.Up && consoleHistory.Count > 0)
            {
                consoleHistoryPos = Math.Max(0, consoleHistoryPos - 1);
                consoleIn.Text = consoleHistory[consoleHistoryPos];
                consoleIn.SelectionStart = consoleIn.TextLength;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down && consoleHistory.Count > 0)
            {
                consoleHistoryPos = Math.Min(consoleHistory.Count, consoleHistoryPos + 1);
                consoleIn.Text = consoleHistoryPos < consoleHistory.Count ? consoleHistory[consoleHistoryPos] : "";
                consoleIn.SelectionStart = consoleIn.TextLength;
                e.Handled = true;
            }
        }

        private async void SendConsoleCommand()
        {
            string cmd = consoleIn.Text.Trim();
            if (cmd.Length == 0 || !connected || sending) return;
            consoleHistory.Remove(cmd);
            consoleHistory.Add(cmd);
            consoleHistoryPos = consoleHistory.Count;
            consoleIn.Clear();

            // Network searches and registrations answer late; everything else is quick.
            int timeout = cmd.IndexOf("COPS=", StringComparison.OrdinalIgnoreCase) >= 0 ? 240000 : 15000;
            sending = true;
            UpdateEnabled();
            try
            {
                await Task.Run(() => modem.At.Send(cmd, timeout));
                modem.InvalidateSlowItems();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!closing) ConnectionLost(ex);
            }
            finally
            {
                sending = false;
                if (!closing)
                {
                    UpdateEnabled();
                    consoleIn.Focus();
                }
            }
        }
    }
}
