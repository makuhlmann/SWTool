using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace SWTool
{
    /// <summary>Flicker free ListView with Ctrl+C / Ctrl+A support.</summary>
    internal sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
        }

        /// <summary>Index of a column that takes the remaining width, or -1.</summary>
        public int FillColumn = -1;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem it in Items) it.Selected = true;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        public void CopySelection()
        {
            StringBuilder sb = new StringBuilder();
            foreach (ListViewItem it in Items)
            {
                if (!it.Selected) continue;
                for (int i = 0; i < it.SubItems.Count; i++)
                {
                    if (i > 0) sb.Append('\t');
                    sb.Append(it.SubItems[i].Text);
                }
                sb.AppendLine();
            }
            if (sb.Length > 0)
            {
                try { Clipboard.SetText(sb.ToString()); } catch { }
            }
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            if (FillColumn < 0 || FillColumn >= Columns.Count) return;
            int used = 0;
            for (int i = 0; i < Columns.Count; i++) if (i != FillColumn) used += Columns[i].Width;
            int w = ClientSize.Width - used - 2;
            if (w > 40 && Columns[FillColumn].Width != w) Columns[FillColumn].Width = w;
        }

        /// <summary>Updates the list in place so that selection and scroll position survive a refresh.</summary>
        public void SetRows(IList<string[]> rows, IList<string> groups)
        {
            while (Items.Count > rows.Count) Items.RemoveAt(Items.Count - 1);
            for (int i = 0; i < rows.Count; i++)
            {
                string[] row = rows[i];
                ListViewItem it;
                if (i < Items.Count) it = Items[i];
                else { it = new ListViewItem(row[0] ?? ""); Items.Add(it); }
                while (it.SubItems.Count < row.Length) it.SubItems.Add("");
                for (int c = 0; c < row.Length; c++)
                {
                    string v = row[c] ?? "";
                    if (it.SubItems[c].Text != v) it.SubItems[c].Text = v;
                }
                if (groups != null)
                {
                    ListViewGroup g = FindGroup(groups[i]);
                    if (it.Group != g) it.Group = g;
                }
            }
        }

        private ListViewGroup FindGroup(string header)
        {
            foreach (ListViewGroup g in Groups) if (g.Header == header) return g;
            ListViewGroup ng = new ListViewGroup(header);
            Groups.Add(ng);
            return ng;
        }
    }

    /// <summary>Header of the status page: signal bars plus three lines of text.</summary>
    internal sealed class HeaderPanel : Control
    {
        private int level;
        private Color levelColour = Color.Gray;
        private string title = "", line2 = "", line3 = "";
        private readonly Font titleFont = new Font("Segoe UI Semibold", 17f);
        private readonly Font lineFont = new Font("Segoe UI", 10f);

        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.White;
        }

        public void Set(int bars, Color colour, string titleText, string second, string third)
        {
            if (bars == level && colour == levelColour && titleText == title && second == line2 && third == line3) return;
            level = bars; levelColour = colour; title = titleText ?? ""; line2 = second ?? ""; line3 = third ?? "";
            Invalidate();
        }

        public static void DrawBars(Graphics g, RectangleF area, int bars, Color on, Color off)
        {
            float gap = area.Width * 0.07f;
            float bw = (area.Width - 4 * gap) / 5f;
            for (int i = 0; i < 5; i++)
            {
                float h = area.Height * (0.28f + 0.18f * i);
                RectangleF r = new RectangleF(area.Left + i * (bw + gap), area.Bottom - h, bw, h);
                using (SolidBrush b = new SolidBrush(i < bars ? on : off)) g.FillRectangle(b, r);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float sc = g.DpiX / 96f;
            g.Clear(BackColor);

            RectangleF bars = new RectangleF(18 * sc, 18 * sc, 66 * sc, Height - 38 * sc);
            DrawBars(g, bars, level, levelColour, Color.FromArgb(0xDD, 0xDD, 0xDD));

            int x = (int)(bars.Right + 20 * sc);
            int w = Math.Max(10, Width - x - 8);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(x - (int)(3 * sc), (int)(6 * sc), w, (int)(36 * sc)),
                Color.FromArgb(0x20, 0x20, 0x20), flags);
            TextRenderer.DrawText(g, line2, lineFont, new Rectangle(x, (int)(44 * sc), w, (int)(22 * sc)),
                Color.FromArgb(0x30, 0x30, 0x30), flags);
            TextRenderer.DrawText(g, line3, lineFont, new Rectangle(x, (int)(66 * sc), w, (int)(22 * sc)),
                Color.FromArgb(0x70, 0x70, 0x70), flags);

            using (Pen p = new Pen(Color.FromArgb(0xD8, 0xD8, 0xD8))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); lineFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>One signal metric: caption, value, a quality coloured bar and a rating.</summary>
    internal sealed class SignalMeter : Control
    {
        private string caption = "", valueText = "-", hint = "";
        private double fraction = double.NaN;
        private Quality quality = Quality.Unknown;
        private readonly Font boldFont = new Font("Segoe UI Semibold", 10.5f);

        public SignalMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        }

        public void Set(string name, string description, double? value, string unit, double min, double max, Quality q)
        {
            string vt = value.HasValue ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit : "-";
            double f = value.HasValue ? Math.Max(0.02, Math.Min(1.0, (value.Value - min) / (max - min))) : double.NaN;
            if (name == caption && vt == valueText && q == quality && description == hint) return;
            caption = name; hint = description; valueText = vt; fraction = f; quality = q;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float sc = g.DpiX / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int capW = (int)(52 * sc), valW = (int)(84 * sc), ratW = (int)(72 * sc);
            int mid = Height / 2;
            const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine;
            const TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine;

            TextRenderer.DrawText(g, caption, Font, new Rectangle(0, 0, capW, Height), ForeColor, left);
            TextRenderer.DrawText(g, valueText, boldFont, new Rectangle(capW, 0, valW, Height), ForeColor, right);

            float bx = capW + valW + 12 * sc;
            float bwidth = Width - bx - ratW - 10 * sc;
            if (bwidth > 20)
            {
                float bh = 10 * sc;
                RectangleF track = new RectangleF(bx, mid - bh / 2, bwidth, bh);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0xE2, 0xE2, 0xE2))) FillRound(g, b, track);
                if (!double.IsNaN(fraction))
                {
                    RectangleF fill = new RectangleF(track.Left, track.Top, (float)(track.Width * fraction), track.Height);
                    using (SolidBrush b = new SolidBrush(Rating.Colour(quality))) FillRound(g, b, fill);
                }
            }
            TextRenderer.DrawText(g, Rating.Text(quality), Font, new Rectangle(Width - ratW, 0, ratW, Height),
                Rating.Colour(quality), left);
        }

        private static void FillRound(Graphics g, Brush b, RectangleF r)
        {
            float d = r.Height;
            if (r.Width < d) { g.FillEllipse(b, r.Left, r.Top, Math.Max(r.Width, 2f), d); return; }
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(r.Left, r.Top, d, d, 90, 180);
                p.AddArc(r.Right - d, r.Top, d, d, 270, 180);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) boldFont.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Rolling chart of two signal metrics with independent left and right axes.</summary>
    internal sealed class HistoryChart : Control
    {
        private struct Sample
        {
            public DateTime T;
            public double A, B;
        }

        private readonly List<Sample> samples = new List<Sample>();
        private string nameA = "", unitA = "", nameB = "", unitB = "";
        private double minA = -130, maxA = -60, minB = -10, maxB = 30;
        private static readonly Color ColA = Color.FromArgb(0x1F, 0x6F, 0xC4);
        private static readonly Color ColB = Color.FromArgb(0xE0, 0x7B, 0x1A);

        public TimeSpan Window = TimeSpan.FromMinutes(10);

        public HistoryChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.White;
        }

        /// <summary>Defines the two series and clears the history. Pass null as second name for a single series.</summary>
        public void Configure(string a, string aUnit, double aMin, double aMax, string b, string bUnit, double bMin, double bMax)
        {
            nameA = a ?? ""; unitA = aUnit ?? ""; minA = aMin; maxA = aMax;
            nameB = b ?? ""; unitB = bUnit ?? ""; minB = bMin; maxB = bMax;
            samples.Clear();
            Invalidate();
        }

        public void Add(DateTime t, double? a, double? b)
        {
            Sample s;
            s.T = t;
            s.A = a.HasValue ? a.Value : double.NaN;
            s.B = b.HasValue ? b.Value : double.NaN;
            samples.Add(s);
            DateTime limit = t - Window;
            int drop = 0;
            while (drop < samples.Count && samples[drop].T < limit) drop++;
            if (drop > 0) samples.RemoveRange(0, drop);
            Invalidate();
        }

        public void Clear()
        {
            samples.Clear();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float sc = g.DpiX / 96f;
            g.Clear(BackColor);
            bool hasB = nameB.Length > 0;

            RectangleF plot = new RectangleF(44 * sc, 24 * sc, Width - (hasB ? 84 : 56) * sc, Height - 46 * sc);
            if (plot.Width < 40 || plot.Height < 30) return;

            CultureInfo inv = CultureInfo.InvariantCulture;
            Color axis = Color.FromArgb(0x80, 0x80, 0x80);
            const int divisions = 4;
            using (Pen grid = new Pen(Color.FromArgb(0xE6, 0xE6, 0xE6)))
            using (Pen border = new Pen(Color.FromArgb(0xC0, 0xC0, 0xC0)))
            {
                for (int i = 0; i <= divisions; i++)
                {
                    float y = plot.Bottom - plot.Height * i / divisions;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    string la = (minA + (maxA - minA) * i / divisions).ToString("0", inv);
                    TextRenderer.DrawText(g, la, Font, new Rectangle(0, (int)(y - 9 * sc), (int)(plot.Left - 5 * sc), (int)(18 * sc)),
                        ColA, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    if (hasB)
                    {
                        string lb = (minB + (maxB - minB) * i / divisions).ToString("0", inv);
                        TextRenderer.DrawText(g, lb, Font,
                            new Rectangle((int)(plot.Right + 5 * sc), (int)(y - 9 * sc), (int)(34 * sc), (int)(18 * sc)),
                            ColB, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    }
                }
                int minutes = Math.Max(1, (int)Math.Round(Window.TotalMinutes));
                int stepMin = minutes > 5 ? 2 : 1;
                for (int m = 0; m <= minutes; m += stepMin)
                {
                    float x = plot.Right - plot.Width * m / minutes;
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    string lt = m == 0 ? "now" : "-" + m + " min";
                    TextRenderer.DrawText(g, lt, Font,
                        new Rectangle((int)(x - 30 * sc), (int)(plot.Bottom + 2 * sc), (int)(60 * sc), (int)(18 * sc)),
                        axis, TextFormatFlags.HorizontalCenter);
                }
                g.DrawRectangle(border, plot.Left, plot.Top, plot.Width, plot.Height);
            }

            // Legend with the latest values.
            double lastA = double.NaN, lastB = double.NaN;
            if (samples.Count > 0) { lastA = samples[samples.Count - 1].A; lastB = samples[samples.Count - 1].B; }
            int lx = (int)plot.Left;
            lx = DrawLegend(g, lx, (int)(3 * sc), ColA, nameA, unitA, lastA, sc);
            if (hasB) DrawLegend(g, lx + (int)(18 * sc), (int)(3 * sc), ColB, nameB, unitB, lastB, sc);

            if (samples.Count == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DateTime now = samples[samples.Count - 1].T;
            Region oldClip = g.Clip;
            g.SetClip(RectangleF.Inflate(plot, 1, 1));
            if (hasB) DrawSeries(g, plot, now, false, ColB, minB, maxB, sc);
            DrawSeries(g, plot, now, true, ColA, minA, maxA, sc);
            g.Clip = oldClip;
        }

        private int DrawLegend(Graphics g, int x, int y, Color c, string name, string unit, double last, float sc)
        {
            string text = name + (double.IsNaN(last) ? "" : "  " + last.ToString("0.#", CultureInfo.InvariantCulture) + " " + unit);
            using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, x, y + 5 * sc, 12 * sc, 4 * sc);
            Size size = TextRenderer.MeasureText(g, text, Font);
            TextRenderer.DrawText(g, text, Font, new Point(x + (int)(16 * sc), y), c);
            return x + (int)(16 * sc) + size.Width;
        }

        private void DrawSeries(Graphics g, RectangleF plot, DateTime now, bool first, Color colour, double min, double max, float sc)
        {
            List<PointF> run = new List<PointF>();
            DateTime prev = DateTime.MinValue;
            using (Pen pen = new Pen(colour, 1.8f * sc))
            using (SolidBrush dot = new SolidBrush(colour))
            {
                pen.LineJoin = LineJoin.Round;
                for (int i = 0; i <= samples.Count; i++)
                {
                    bool end = i == samples.Count;
                    double v = end ? double.NaN : (first ? samples[i].A : samples[i].B);
                    bool gap = end || double.IsNaN(v) || (run.Count > 0 && (samples[i].T - prev).TotalSeconds > 45);
                    if (gap)
                    {
                        if (run.Count > 1) g.DrawLines(pen, run.ToArray());
                        else if (run.Count == 1) g.FillEllipse(dot, run[0].X - 2 * sc, run[0].Y - 2 * sc, 4 * sc, 4 * sc);
                        run.Clear();
                        if (end || double.IsNaN(v)) continue;
                    }
                    double age = (now - samples[i].T).TotalSeconds / Window.TotalSeconds;
                    double f = Math.Max(0, Math.Min(1, (v - min) / (max - min)));
                    run.Add(new PointF((float)(plot.Right - plot.Width * age), (float)(plot.Bottom - plot.Height * f)));
                    prev = samples[i].T;
                }
            }
        }
    }
}
