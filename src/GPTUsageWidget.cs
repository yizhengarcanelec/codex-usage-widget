using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Usage Widget")]
[assembly: AssemblyDescription("A local Codex token and weekly quota widget for Windows")]
[assembly: AssemblyProduct("Codex Usage Widget")]
[assembly: AssemblyVersion("0.3.0.0")]
[assembly: AssemblyFileVersion("0.3.0.0")]

internal sealed class UsageSnapshot
{
    public long Input;
    public long Cached;
    public long Output;
    public long Reasoning;
    public long Total;
    public bool Partial;
    public bool HasWeekly;
    public double UsedPercent;
    public double RemainingPercent;
    public DateTimeOffset ResetAt;
    public string Plan;
    public DateTimeOffset GeneratedAt;
}

internal static class UsageReader
{
    private static IDictionary<string, object> Dict(object value)
    {
        return value as IDictionary<string, object>;
    }

    private static long LongValue(IDictionary<string, object> data, string key)
    {
        object value;
        if (data == null || !data.TryGetValue(key, out value) || value == null) return 0;
        try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static double DoubleValue(IDictionary<string, object> data, string key)
    {
        object value;
        if (data == null || !data.TryGetValue(key, out value) || value == null) return 0;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static string StringValue(IDictionary<string, object> data, string key)
    {
        object value;
        if (data == null || !data.TryGetValue(key, out value) || value == null) return "";
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    public static UsageSnapshot Read()
    {
        var result = new UsageSnapshot { GeneratedAt = DateTimeOffset.Now };
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        string codexRoot = String.IsNullOrWhiteSpace(configured) ? Path.Combine(profile, ".codex") : Path.GetFullPath(configured);
        string[] roots = { Path.Combine(codexRoot, "sessions"), Path.Combine(codexRoot, "archived_sessions") };
        DateTime today = DateTime.Today;
        DateTime tomorrow = today.AddDays(1);
        DateTimeOffset? earliest = null;
        DateTimeOffset? latestWeeklyStamp = null;
        IDictionary<string, object> latestWeekly = null;
        string latestPlan = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToArray(); }
            catch { continue; }

            foreach (string file in files)
            {
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) continue;
                            IDictionary<string, object> record;
                            try { record = Dict(serializer.DeserializeObject(line)); }
                            catch { continue; }
                            if (record == null) continue;

                            IDictionary<string, object> payload;
                            object payloadObj;
                            if (!record.TryGetValue("payload", out payloadObj) || (payload = Dict(payloadObj)) == null) continue;
                            if (!String.Equals(StringValue(payload, "type"), "token_count", StringComparison.Ordinal)) continue;

                            DateTimeOffset stamp;
                            try { stamp = DateTimeOffset.Parse(StringValue(record, "timestamp"), CultureInfo.InvariantCulture); }
                            catch { continue; }
                            if (!earliest.HasValue || stamp < earliest.Value) earliest = stamp;

                            IDictionary<string, object> info = null;
                            object infoObj;
                            if (payload.TryGetValue("info", out infoObj)) info = Dict(infoObj);
                            IDictionary<string, object> usage = null;
                            object usageObj;
                            if (info != null && info.TryGetValue("last_token_usage", out usageObj)) usage = Dict(usageObj);

                            DateTime local = stamp.LocalDateTime;
                            if (usage != null && local >= today && local < tomorrow)
                            {
                                string eventKey = Path.GetFileName(file) + "|" + stamp.ToString("O") + "|" + LongValue(usage, "total_tokens") + "|" + LongValue(usage, "input_tokens");
                                if (seen.Add(eventKey))
                                {
                                    result.Input += LongValue(usage, "input_tokens");
                                    result.Cached += LongValue(usage, "cached_input_tokens");
                                    result.Output += LongValue(usage, "output_tokens");
                                    result.Reasoning += LongValue(usage, "reasoning_output_tokens");
                                    result.Total += LongValue(usage, "total_tokens");
                                }
                            }

                            IDictionary<string, object> rate = null;
                            object rateObj;
                            if (payload.TryGetValue("rate_limits", out rateObj)) rate = Dict(rateObj);
                            if (rate == null) continue;

                            IDictionary<string, object> selectedWindow = null;
                            foreach (string key in new[] { "primary", "secondary" })
                            {
                                object windowObj;
                                IDictionary<string, object> candidate;
                                if (!rate.TryGetValue(key, out windowObj) || (candidate = Dict(windowObj)) == null) continue;
                                if (LongValue(candidate, "window_minutes") < 10000) continue;
                                if (selectedWindow == null || LongValue(candidate, "window_minutes") > LongValue(selectedWindow, "window_minutes"))
                                    selectedWindow = candidate;
                            }

                            if (selectedWindow != null && (!latestWeeklyStamp.HasValue || stamp > latestWeeklyStamp.Value))
                            {
                                latestWeeklyStamp = stamp;
                                latestWeekly = selectedWindow;
                                latestPlan = StringValue(rate, "plan_type");
                            }
                        }
                    }
                }
                catch { }
            }
        }

        result.Partial = earliest.HasValue && earliest.Value.LocalDateTime > today.AddMinutes(5);
        if (latestWeekly != null)
        {
            result.HasWeekly = true;
            result.UsedPercent = Math.Round(DoubleValue(latestWeekly, "used_percent"), 1);
            result.RemainingPercent = Math.Round(Math.Max(0, 100 - result.UsedPercent), 1);
            long seconds = LongValue(latestWeekly, "resets_at");
            result.ResetAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds).ToLocalTime();
            result.Plan = latestPlan;
        }
        return result;
    }
}

internal sealed class WidgetForm : Form
{
    private readonly Label todayValue;
    private readonly Label inputValue;
    private readonly Label outputValue;
    private readonly Label cacheValue;
    private readonly Label weeklyValue;
    private readonly Label weeklyDetail;
    private readonly Label resetValue;
    private readonly Label statusValue;
    private readonly Panel progressFill;
    private readonly Panel progressTrack;
    private readonly Button pinButton;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly ToolTip toolTip;
    private readonly List<Control> detailControls = new List<Control>();
    private bool refreshing;
    private double lastRemainingPercent;
    private bool compactMode;
    private string compactText = "--%";
    private Color compactAccent = Green;

    private const int CompactDiameter = 144;
    private const int DetailMinWidth = 320;
    private const int DetailMinHeight = 250;

    private static readonly Color Bg = Color.FromArgb(16, 20, 28);
    private static readonly Color Card = Color.FromArgb(23, 28, 38);
    private static readonly Color Border = Color.FromArgb(42, 49, 64);
    private static readonly Color White = Color.FromArgb(246, 248, 252);
    private static readonly Color Muted = Color.FromArgb(126, 139, 159);
    private static readonly Color Green = Color.FromArgb(74, 222, 128);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    public WidgetForm(bool startCompact)
    {
        Text = "Codex Usage Widget";
        ClientSize = new Size(360, 286);
        MinimumSize = new Size(CompactDiameter, CompactDiameter);
        MaximumSize = new Size(480, 360);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Bg;
        ForeColor = White;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Icon = SystemIcons.Application;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateStyles();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);

        var work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - Width - 20, work.Bottom - Height - 20);

        Controls.Add(MakeLabel("●", 17, 12, 18, 20, 11F, Green, FontStyle.Bold));
        Controls.Add(MakeLabel("CODEX METER", 36, 15, 130, 20, 10F, White, FontStyle.Bold));

        pinButton = MakeButton("PIN", 283, 12, 40, 23);
        var closeButton = MakeButton("X", 328, 12, 24, 23);
        pinButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(pinButton);
        Controls.Add(closeButton);
        pinButton.Click += delegate { TopMost = !TopMost; pinButton.Text = TopMost ? "PIN" : "FREE"; pinButton.ForeColor = TopMost ? Muted : Color.FromArgb(251, 191, 36); };
        closeButton.Click += delegate { Close(); };

        Controls.Add(MakeLabel("TOKENS TODAY", 18, 54, 140, 18, 8.5F, Muted, FontStyle.Bold));
        todayValue = MakeLabel("--", 16, 69, 180, 45, 27F, White, FontStyle.Bold);
        Controls.Add(todayValue);

        var breakdownTitle = MakeLabel("BREAKDOWN", 236, 54, 105, 18, 8.5F, Muted, FontStyle.Bold);
        breakdownTitle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(breakdownTitle);
        inputValue = MakeLabel("IN   --", 236, 75, 110, 17, 9.5F, Color.FromArgb(180, 190, 205), FontStyle.Regular);
        outputValue = MakeLabel("OUT  --", 236, 93, 110, 17, 9.5F, Color.FromArgb(180, 190, 205), FontStyle.Regular);
        cacheValue = MakeLabel("CACHE --", 236, 111, 110, 16, 8.5F, Color.FromArgb(105, 115, 135), FontStyle.Regular);
        inputValue.Anchor = outputValue.Anchor = cacheValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(inputValue); Controls.Add(outputValue); Controls.Add(cacheValue);

        var card = new Panel { Location = new Point(16, 135), Size = new Size(328, 108), BackColor = Card, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        card.Paint += delegate(object s, PaintEventArgs e) { using (var p = new Pen(Color.FromArgb(37, 44, 57))) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); };
        card.Resize += delegate { card.Invalidate(); };
        Controls.Add(card);
        card.Controls.Add(MakeLabel("WEEKLY LEFT", 13, 12, 130, 18, 8.5F, Muted, FontStyle.Bold));
        weeklyValue = MakeLabel("--%", 222, 8, 90, 27, 17F, Green, FontStyle.Bold);
        weeklyValue.TextAlign = ContentAlignment.MiddleRight;
        weeklyValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        card.Controls.Add(weeklyValue);

        progressTrack = new Panel { Location = new Point(13, 43), Size = new Size(302, 7), BackColor = Color.FromArgb(41, 49, 64), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        progressFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 7), BackColor = Green };
        progressTrack.Controls.Add(progressFill);
        card.Controls.Add(progressTrack);
        weeklyDetail = MakeLabel("Waiting for local data", 13, 62, 170, 18, 8.5F, Color.FromArgb(145, 156, 175), FontStyle.Regular);
        resetValue = MakeLabel("", 189, 62, 126, 18, 8.5F, Color.FromArgb(105, 115, 135), FontStyle.Regular);
        resetValue.TextAlign = ContentAlignment.MiddleRight;
        resetValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        card.Controls.Add(weeklyDetail); card.Controls.Add(resetValue);

        statusValue = MakeLabel("Starting...", 18, 257, 260, 18, 8F, Color.FromArgb(94, 107, 128), FontStyle.Regular);
        statusValue.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(statusValue);
        var localOnly = MakeLabel("LOCAL ONLY", 279, 257, 65, 18, 7.5F, Color.FromArgb(62, 72, 89), FontStyle.Bold);
        localOnly.TextAlign = ContentAlignment.MiddleRight;
        localOnly.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(localOnly);

        foreach (Control control in Controls) detailControls.Add(control);

        toolTip = new ToolTip();
        MouseDown += DragWindow;
        DoubleClick += ToggleSizeMode;
        foreach (Control control in Controls) if (!(control is Button)) control.MouseDown += DragWindow;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Expand / compact", null, delegate { ToggleSizeMode(this, EventArgs.Empty); });
        menu.Items.Add("Refresh now", null, delegate { RefreshSnapshot(); });
        menu.Items.Add("Toggle always on top", null, delegate { pinButton.PerformClick(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Close", null, delegate { Close(); });
        ContextMenuStrip = menu;

        refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        refreshTimer.Tick += delegate { RefreshSnapshot(); };
        Shown += delegate { UpdateLayoutMode(); refreshTimer.Start(); RefreshSnapshot(); };
        if (startCompact)
        {
            ClientSize = new Size(CompactDiameter, CompactDiameter);
            Location = new Point(work.Right - Width - 20, work.Bottom - Height - 20);
        }
    }

    private static Label MakeLabel(string text, int x, int y, int width, int height, float size, Color color, FontStyle style)
    {
        return new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), ForeColor = color, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, style), AutoEllipsis = true };
    }

    private static Button MakeButton(string text, int x, int y, int width, int height)
    {
        var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(width, height), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(26, 32, 43), ForeColor = Color.FromArgb(154, 166, 184), Font = new Font("Segoe UI", 7.5F, FontStyle.Bold), TabStop = false };
        button.FlatAppearance.BorderColor = Color.FromArgb(48, 57, 73);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 45, 59);
        return button;
    }

    private void DragWindow(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks > 1) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    private void ToggleSizeMode(object sender, EventArgs e)
    {
        Rectangle before = Bounds;
        ClientSize = compactMode ? new Size(360, 286) : new Size(CompactDiameter, CompactDiameter);
        Rectangle work = Screen.FromRectangle(before).WorkingArea;
        int x = Math.Max(work.Left, Math.Min(work.Right - Width, before.Right - Width));
        int y = Math.Max(work.Top, Math.Min(work.Bottom - Height, before.Bottom - Height));
        Location = new Point(x, y);
    }

    private void UpdateLayoutMode()
    {
        if (detailControls.Count == 0) return;
        bool nextCompact = ClientSize.Width < DetailMinWidth || ClientSize.Height < DetailMinHeight;
        compactMode = nextCompact;

        foreach (Control control in detailControls) control.Visible = !compactMode;

        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        GraphicsPath path;
        bool circular = compactMode && Math.Abs(ClientSize.Width - ClientSize.Height) <= 8;
        if (circular)
        {
            path = new GraphicsPath();
            path.AddEllipse(0, 0, ClientSize.Width, ClientSize.Height);
        }
        else
        {
            int radius = compactMode ? Math.Min(ClientSize.Width, ClientSize.Height) / 2 : 12;
            path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), radius);
        }

        Region oldRegion = Region;
        Region = new Region(path);
        if (oldRegion != null) oldRegion.Dispose();
        path.Dispose();
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(2, radius * 2);
        Rectangle arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string Compact(long value)
    {
        if (value >= 1000000000) return (value / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "B";
        if (value >= 1000000) return (value / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "M";
        if (value >= 1000) return (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private async void RefreshSnapshot()
    {
        if (refreshing || IsDisposed) return;
        refreshing = true;
        statusValue.Text = "Refreshing local Codex data...";
        try
        {
            UsageSnapshot snapshot = await Task.Run((Func<UsageSnapshot>)UsageReader.Read);
            if (IsDisposed) return;
            todayValue.Text = Compact(snapshot.Total);
            toolTip.SetToolTip(todayValue, snapshot.Total.ToString("N0", CultureInfo.InvariantCulture) + " tokens");
            inputValue.Text = "IN   " + Compact(snapshot.Input);
            outputValue.Text = "OUT  " + Compact(snapshot.Output);
            cacheValue.Text = "CACHE " + Compact(snapshot.Cached);

            if (snapshot.HasWeekly)
            {
                weeklyValue.Text = snapshot.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                compactText = weeklyValue.Text;
                lastRemainingPercent = snapshot.RemainingPercent;
                string plan = String.IsNullOrEmpty(snapshot.Plan) ? "CODEX" : snapshot.Plan.ToUpperInvariant();
                weeklyDetail.Text = snapshot.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% used  |  " + plan;
                resetValue.Text = "RESET " + snapshot.ResetAt.ToString("MM-dd HH:mm");
                progressFill.Width = Math.Max(0, Math.Min(progressTrack.Width, (int)Math.Round(progressTrack.Width * snapshot.RemainingPercent / 100.0)));
                Color accent = snapshot.RemainingPercent > 50 ? Green : snapshot.RemainingPercent > 20 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(251, 113, 133);
                progressFill.BackColor = accent;
                weeklyValue.ForeColor = accent;
                compactAccent = accent;
            }
            else
            {
                weeklyValue.Text = "--%";
                compactText = "--%";
                compactAccent = Muted;
                weeklyDetail.Text = "No weekly sample yet";
                resetValue.Text = "";
                progressFill.Width = 0;
                lastRemainingPercent = 0;
            }
            statusValue.Text = "Updated " + snapshot.GeneratedAt.ToString("HH:mm:ss") + " | every 5s" + (snapshot.Partial ? " | partial history" : "");
            Invalidate();
        }
        catch
        {
            statusValue.Text = "Waiting for Codex session data...";
        }
        finally { refreshing = false; }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1)
            {
                Point cursor = PointToClient(Cursor.Position);
                const int grip = 8;
                bool left = cursor.X <= grip;
                bool right = cursor.X >= ClientSize.Width - grip;
                bool top = cursor.Y <= grip;
                bool bottom = cursor.Y >= ClientSize.Height - grip;
                if (left && top) { m.Result = (IntPtr)13; return; }
                if (right && top) { m.Result = (IntPtr)14; return; }
                if (left && bottom) { m.Result = (IntPtr)16; return; }
                if (right && bottom) { m.Result = (IntPtr)17; return; }
                if (left) { m.Result = (IntPtr)10; return; }
                if (right) { m.Result = (IntPtr)11; return; }
                if (top) { m.Result = (IntPtr)12; return; }
                if (bottom) { m.Result = (IntPtr)15; return; }
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateLayoutMode();
        if (progressTrack != null && progressFill != null)
            progressFill.Width = Math.Max(0, Math.Min(progressTrack.Width, (int)Math.Round(progressTrack.Width * lastRemainingPercent / 100.0)));
        Invalidate(true);
        if (IsHandleCreated)
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0400);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Bg);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (compactMode)
        {
            int inset = Math.Max(8, Math.Min(ClientSize.Width, ClientSize.Height) / 14);
            int diameter = Math.Min(ClientSize.Width, ClientSize.Height) - inset * 2;
            var ringBounds = new Rectangle((ClientSize.Width - diameter) / 2, (ClientSize.Height - diameter) / 2, diameter, diameter);
            using (var track = new Pen(Color.FromArgb(42, 49, 64), Math.Max(6, diameter / 18)))
                e.Graphics.DrawEllipse(track, ringBounds);
            Color accent = compactAccent;
            using (var progress = new Pen(accent, Math.Max(6, diameter / 18)))
            {
                progress.StartCap = LineCap.Round;
                progress.EndCap = LineCap.Round;
                float sweep = (float)Math.Max(0, Math.Min(359.9, 360.0 * lastRemainingPercent / 100.0));
                if (sweep > 0) e.Graphics.DrawArc(progress, ringBounds, -90, sweep);
            }
            float fontSize = Math.Max(24F, Math.Min(38F, Math.Min(ClientSize.Width, ClientSize.Height) * 0.24F));
            using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            using (var brush = new SolidBrush(compactAccent))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                e.Graphics.DrawString(compactText, font, brush, ClientRectangle, format);
        }
        else
        {
            using (GraphicsPath borderPath = RoundedPath(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 12))
            using (var pen = new Pen(Color.FromArgb(42, 49, 64)))
                e.Graphics.DrawPath(pen, borderPath);
        }
    }
}

internal static class Program
{
    private static Mutex instanceMutex;

    [STAThread]
    private static void Main()
    {
        bool created;
        instanceMutex = new Mutex(true, "Local\\GPTUsageWidgetNative", out created);
        if (!created) return;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool startCompact = Environment.GetCommandLineArgs().Any(arg => String.Equals(arg, "--compact", StringComparison.OrdinalIgnoreCase));
        try { Application.Run(new WidgetForm(startCompact)); }
        finally { instanceMutex.ReleaseMutex(); instanceMutex.Dispose(); }
    }
}
