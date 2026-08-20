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
[assembly: AssemblyVersion("0.4.0.0")]
[assembly: AssemblyFileVersion("0.4.0.0")]

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

internal sealed class WidgetTheme
{
    public readonly string Name;
    public readonly Color Background;
    public readonly Color Card;
    public readonly Color Border;
    public readonly Color Text;
    public readonly Color Muted;
    public readonly Color SoftText;
    public readonly Color DimText;
    public readonly Color Accent;
    public readonly Color Track;
    public readonly Color Button;

    public WidgetTheme(string name, Color background, Color card, Color border, Color text, Color muted, Color softText, Color dimText, Color accent, Color track, Color button)
    {
        Name = name;
        Background = background;
        Card = card;
        Border = border;
        Text = text;
        Muted = muted;
        SoftText = softText;
        DimText = dimText;
        Accent = accent;
        Track = track;
        Button = button;
    }
}

internal sealed class LanguageToggle : Control
{
    private bool chinese;
    public Color TrackColor = Color.FromArgb(26, 32, 43);
    public Color ActiveColor = Color.FromArgb(74, 222, 128);
    public Color InactiveColor = Color.FromArgb(126, 139, 159);
    public Color BorderColor = Color.FromArgb(48, 57, 73);
    public event EventHandler ValueChanged;

    public bool Chinese
    {
        get { return chinese; }
        set { if (chinese == value) return; chinese = value; Invalidate(); }
    }

    public LanguageToggle()
    {
        Size = new Size(44, 21);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnClick(EventArgs e)
    {
        Chinese = !Chinese;
        EventHandler handler = ValueChanged;
        if (handler != null) handler(this, EventArgs.Empty);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle track = new Rectangle(0, 1, Width - 1, Height - 2);
        using (GraphicsPath path = PillPath(track))
        using (var brush = new SolidBrush(TrackColor))
        using (var pen = new Pen(BorderColor))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }

        int half = Width / 2;
        Rectangle active = Chinese ? new Rectangle(2, 3, half - 2, Height - 6) : new Rectangle(half, 3, Width - half - 2, Height - 6);
        using (GraphicsPath activePath = PillPath(active))
        using (var brush = new SolidBrush(ActiveColor))
            e.Graphics.FillPath(brush, activePath);

        using (var font = new Font("Segoe UI", 6.2F, FontStyle.Bold))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (var activeBrush = new SolidBrush(Color.FromArgb(15, 20, 27)))
        using (var inactiveBrush = new SolidBrush(InactiveColor))
        {
            e.Graphics.DrawString("CN", font, Chinese ? activeBrush : inactiveBrush, new RectangleF(0, 1, half, Height - 2), format);
            e.Graphics.DrawString("EN", font, Chinese ? inactiveBrush : activeBrush, new RectangleF(half, 1, Width - half, Height - 2), format);
        }
    }

    private static GraphicsPath PillPath(Rectangle bounds)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 90, 180);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 180);
        path.CloseFigure();
        return path;
    }
}

internal enum CloseBehavior
{
    Ask,
    Minimize,
    Exit
}

internal sealed class CloseChoiceDialog : Form
{
    public CloseBehavior SelectedBehavior { get; private set; }

    public CloseChoiceDialog()
    {
        Text = "关闭方式";
        ClientSize = new Size(390, 142);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(16, 20, 28);
        ForeColor = Color.FromArgb(246, 248, 252);

        var title = new Label { Text = "点击右上角 × 时，你希望如何处理程序？", Location = new Point(18, 16), Size = new Size(350, 24), ForeColor = ForeColor };
        var hint = new Label { Text = "选择后会记住，也可从右键菜单修改。", Location = new Point(18, 43), Size = new Size(350, 20), ForeColor = Color.FromArgb(150, 163, 184) };
        var minimize = new Button { Text = "最小化至后台", Location = new Point(18, 86), Size = new Size(122, 30), DialogResult = DialogResult.OK };
        var exit = new Button { Text = "彻底结束程序", Location = new Point(148, 86), Size = new Size(122, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Location = new Point(278, 86), Size = new Size(78, 30), DialogResult = DialogResult.Cancel };
        minimize.Click += delegate { SelectedBehavior = CloseBehavior.Minimize; };
        exit.Click += delegate { SelectedBehavior = CloseBehavior.Exit; };
        Controls.Add(title); Controls.Add(hint); Controls.Add(minimize); Controls.Add(exit); Controls.Add(cancel);
        CancelButton = cancel;
    }
}

internal sealed class WidgetForm : Form
{
    private readonly Label brandDot;
    private readonly Label titleLabel;
    private readonly Label todayTitle;
    private readonly Label breakdownTitle;
    private readonly Label weeklyTitle;
    private readonly Label todayValue;
    private readonly Label inputValue;
    private readonly Label outputValue;
    private readonly Label cacheValue;
    private readonly Label weeklyValue;
    private readonly Label weeklyDetail;
    private readonly Label resetValue;
    private readonly Label statusValue;
    private readonly Label localOnly;
    private readonly Panel weeklyCard;
    private readonly Panel progressFill;
    private readonly Panel progressTrack;
    private readonly Button pinButton;
    private readonly Button closeButton;
    private readonly Button themeButton;
    private readonly Button expandButton;
    private readonly LanguageToggle languageToggle;
    private readonly ContextMenuStrip contextMenu;
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip trayMenu;
    private readonly List<ToolStripMenuItem> themeMenuItems = new List<ToolStripMenuItem>();
    private ToolStripMenuItem chineseMenuItem;
    private ToolStripMenuItem englishMenuItem;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer modeTransitionTimer;
    private readonly System.Windows.Forms.Timer themeTransitionTimer;
    private readonly System.Windows.Forms.Timer compactHoverTimer;
    private readonly ToolTip toolTip;
    private readonly List<Control> detailControls = new List<Control>();
    private bool refreshing;
    private double lastRemainingPercent;
    private bool compactMode;
    private bool changingMode;
    private bool chinese;
    private bool lastHasWeekly;
    private int themeIndex;
    private string compactText = "--%";
    private string compactResetText = "";
    private Color compactAccent = Color.FromArgb(74, 222, 128);
    private bool compactHovered;
    private bool compactCollapseRequested;
    private bool exiting;
    private bool handlingCloseRequest;
    private CloseBehavior closeBehavior;
    private WidgetTheme renderedTheme;
    private WidgetTheme themeFrom;
    private WidgetTheme themeTo;
    private DateTime themeTransitionStarted;
    private Rectangle modeStartBounds;
    private Rectangle modeTargetBounds;
    private DateTime modeTransitionStarted;
    private bool modeTargetCompact;
    private bool modeVisualsSwitched;
    private double modeProgress;

    private const int CompactDiameter = 84;
    private const int CompactMaxDiameter = 132;
    private const int PanelWidth = 360;
    private const int PanelHeight = 286;
    private const int PanelMinimumWidth = 196;
    private const int PanelMinimumHeight = 130;
    private const int CompactDragTriggerWidth = 182;
    private const int CompactDragTriggerHeight = 116;
    private const int FullLayoutWidth = 340;
    private const int DetailLayoutHeight = 150;
    private const int WeeklyLayoutHeight = 250;
    private const int StatusLayoutHeight = 278;
    private const int TransitionDurationMs = 180;

    private static readonly WidgetTheme[] Themes =
    {
        new WidgetTheme("GPT Green", Color.FromArgb(16, 20, 28), Color.FromArgb(23, 28, 38), Color.FromArgb(42, 49, 64), Color.FromArgb(246, 248, 252), Color.FromArgb(126, 139, 159), Color.FromArgb(180, 190, 205), Color.FromArgb(94, 107, 128), Color.FromArgb(74, 222, 128), Color.FromArgb(41, 49, 64), Color.FromArgb(26, 32, 43)),
        new WidgetTheme("Ocean Blue", Color.FromArgb(10, 18, 31), Color.FromArgb(16, 29, 48), Color.FromArgb(39, 65, 91), Color.FromArgb(239, 247, 255), Color.FromArgb(119, 145, 170), Color.FromArgb(176, 205, 229), Color.FromArgb(82, 111, 140), Color.FromArgb(56, 189, 248), Color.FromArgb(35, 55, 75), Color.FromArgb(19, 37, 58)),
        new WidgetTheme("Violet Night", Color.FromArgb(21, 16, 33), Color.FromArgb(31, 24, 47), Color.FromArgb(66, 52, 92), Color.FromArgb(247, 243, 255), Color.FromArgb(151, 135, 177), Color.FromArgb(207, 193, 230), Color.FromArgb(112, 94, 139), Color.FromArgb(167, 139, 250), Color.FromArgb(55, 44, 75), Color.FromArgb(38, 29, 56)),
        new WidgetTheme("Rose Graphite", Color.FromArgb(29, 17, 24), Color.FromArgb(42, 23, 33), Color.FromArgb(80, 46, 61), Color.FromArgb(255, 243, 248), Color.FromArgb(171, 128, 147), Color.FromArgb(231, 190, 207), Color.FromArgb(133, 87, 107), Color.FromArgb(244, 114, 182), Color.FromArgb(66, 38, 51), Color.FromArgb(50, 28, 39)),
        new WidgetTheme("Amber Slate", Color.FromArgb(28, 23, 15), Color.FromArgb(42, 33, 20), Color.FromArgb(80, 61, 33), Color.FromArgb(255, 249, 235), Color.FromArgb(169, 145, 103), Color.FromArgb(229, 207, 163), Color.FromArgb(128, 103, 61), Color.FromArgb(251, 191, 36), Color.FromArgb(65, 51, 29), Color.FromArgb(49, 38, 23))
    };

    private WidgetTheme Theme { get { return renderedTheme ?? Themes[themeIndex]; } }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ResizeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public WidgetForm(bool startCompact)
    {
        LoadPreferences();
        renderedTheme = Themes[themeIndex];
        Text = "Codex Usage Widget";
        ClientSize = new Size(PanelWidth, PanelHeight);
        MinimumSize = new Size(CompactDiameter, CompactDiameter);
        MaximumSize = new Size(PanelWidth, PanelHeight);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Icon = LoadApplicationIcon();
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateStyles();
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);

        var work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - Width - 20, work.Bottom - Height - 20);

        brandDot = MakeLabel("●", 17, 12, 18, 20, 11F, Theme.Accent, FontStyle.Bold);
        titleLabel = MakeLabel("CODEX METER", 36, 15, 130, 20, 10F, Theme.Text, FontStyle.Bold);
        Controls.Add(brandDot);
        Controls.Add(titleLabel);

        pinButton = MakeButton("PIN", 283, 12, 40, 23);
        closeButton = MakeButton("X", 328, 12, 24, 23);
        pinButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(pinButton);
        Controls.Add(closeButton);
        pinButton.Click += delegate { TopMost = !TopMost; UpdatePinButton(); };
        closeButton.Click += delegate { HandleCloseRequest(); };
        toolTip = new ToolTip();
        toolTip.SetToolTip(closeButton, "关闭行为");

        languageToggle = new LanguageToggle { Location = new Point(188, 13), Anchor = AnchorStyles.Top | AnchorStyles.Right, Chinese = chinese };
        languageToggle.ValueChanged += delegate { SetLanguage(languageToggle.Chinese); };
        Controls.Add(languageToggle);

        themeButton = MakeButton("◐", 239, 12, 36, 23);
        themeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        themeButton.Click += delegate { SetTheme((themeIndex + 1) % Themes.Length); };
        Controls.Add(themeButton);

        todayTitle = MakeLabel("TOKENS TODAY", 18, 54, 140, 18, 8.5F, Theme.Muted, FontStyle.Bold);
        Controls.Add(todayTitle);
        todayValue = MakeLabel("--", 16, 69, 180, 45, 27F, Theme.Text, FontStyle.Bold);
        Controls.Add(todayValue);

        breakdownTitle = MakeLabel("BREAKDOWN", 236, 54, 105, 18, 8.5F, Theme.Muted, FontStyle.Bold);
        breakdownTitle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(breakdownTitle);
        inputValue = MakeLabel("IN   --", 236, 75, 110, 17, 9.5F, Theme.SoftText, FontStyle.Regular);
        outputValue = MakeLabel("OUT  --", 236, 93, 110, 17, 9.5F, Theme.SoftText, FontStyle.Regular);
        cacheValue = MakeLabel("CACHE --", 236, 111, 110, 16, 8.5F, Theme.DimText, FontStyle.Regular);
        inputValue.Anchor = outputValue.Anchor = cacheValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(inputValue); Controls.Add(outputValue); Controls.Add(cacheValue);

        weeklyCard = new Panel { Location = new Point(16, 135), Size = new Size(328, 108), BackColor = Theme.Card, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        weeklyCard.Paint += delegate(object s, PaintEventArgs e) { using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, weeklyCard.Width - 1, weeklyCard.Height - 1); };
        weeklyCard.Resize += delegate { weeklyCard.Invalidate(); };
        Controls.Add(weeklyCard);
        weeklyTitle = MakeLabel("WEEKLY LEFT", 13, 12, 130, 18, 8.5F, Theme.Muted, FontStyle.Bold);
        weeklyCard.Controls.Add(weeklyTitle);
        weeklyValue = MakeLabel("--%", 222, 8, 90, 27, 17F, Theme.Accent, FontStyle.Bold);
        weeklyValue.TextAlign = ContentAlignment.MiddleRight;
        weeklyValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        weeklyCard.Controls.Add(weeklyValue);

        progressTrack = new Panel { Location = new Point(13, 43), Size = new Size(302, 7), BackColor = Theme.Track, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        progressFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 7), BackColor = Theme.Accent };
        progressTrack.Controls.Add(progressFill);
        weeklyCard.Controls.Add(progressTrack);
        weeklyDetail = MakeLabel("Waiting for local data", 13, 62, 170, 18, 8.5F, Theme.SoftText, FontStyle.Regular);
        resetValue = MakeLabel("", 189, 62, 126, 18, 8.5F, Theme.DimText, FontStyle.Regular);
        resetValue.TextAlign = ContentAlignment.MiddleRight;
        resetValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        weeklyCard.Controls.Add(weeklyDetail); weeklyCard.Controls.Add(resetValue);

        statusValue = MakeLabel("Starting...", 18, 257, 260, 18, 8F, Theme.DimText, FontStyle.Regular);
        statusValue.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(statusValue);
        localOnly = MakeLabel("LOCAL ONLY", 279, 257, 65, 18, 7.5F, Theme.DimText, FontStyle.Bold);
        localOnly.TextAlign = ContentAlignment.MiddleRight;
        localOnly.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(localOnly);

        foreach (Control control in Controls) detailControls.Add(control);

        expandButton = MakeButton("▲", 0, 0, 22, 18);
        expandButton.FlatAppearance.BorderSize = 0;
        expandButton.BackColor = Color.FromArgb(28, 34, 45);
        expandButton.ForeColor = Color.FromArgb(174, 184, 199);
        expandButton.Font = new Font("Segoe UI", 6.5F, FontStyle.Bold);
        expandButton.Visible = false;
        expandButton.Click += delegate { SetCompactMode(false); };
        Controls.Add(expandButton);

        toolTip.SetToolTip(expandButton, "Back to full panel");
        MouseDown += DragWindow;
        DoubleClick += ToggleSizeMode;
        foreach (Control control in Controls) if (!(control is Button) && control != languageToggle) control.MouseDown += DragWindow;

        contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("展开 / 紧凑", null, delegate { ToggleSizeMode(this, EventArgs.Empty); });
        contextMenu.Items.Add("立即刷新", null, delegate { RefreshSnapshot(); });
        contextMenu.Items.Add("始终置顶", null, delegate { pinButton.PerformClick(); });

        var languageMenu = new ToolStripMenuItem("语言 / Language");
        chineseMenuItem = new ToolStripMenuItem("中文", null, delegate { SetLanguage(true); });
        englishMenuItem = new ToolStripMenuItem("English", null, delegate { SetLanguage(false); });
        languageMenu.DropDownItems.Add(chineseMenuItem);
        languageMenu.DropDownItems.Add(englishMenuItem);
        contextMenu.Items.Add(languageMenu);

        var themeMenu = new ToolStripMenuItem("主题 / Theme");
        for (int i = 0; i < Themes.Length; i++)
        {
            int selectedTheme = i;
            var item = new ToolStripMenuItem(Themes[i].Name, null, delegate { SetTheme(selectedTheme); });
            themeMenuItems.Add(item);
            themeMenu.DropDownItems.Add(item);
        }
        contextMenu.Items.Add(themeMenu);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("最小化至后台", null, delegate { MinimizeToBackground(); });
        contextMenu.Items.Add("彻底结束程序", null, delegate { ExitApplication(); });
        contextMenu.Items.Add(BuildCloseBehaviorMenu());
        ContextMenuStrip = contextMenu;

        trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示窗口", null, delegate { RestoreFromTray(); });
        trayMenu.Items.Add("彻底结束程序", null, delegate { ExitApplication(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(BuildCloseBehaviorMenu());
        trayIcon = new NotifyIcon { Icon = Icon, Text = "Codex Usage Widget", Visible = false, ContextMenuStrip = trayMenu };
        trayIcon.DoubleClick += delegate { RestoreFromTray(); };

        refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        refreshTimer.Tick += delegate { RefreshSnapshot(); };
        modeTransitionTimer = new System.Windows.Forms.Timer { Interval = 15 };
        modeTransitionTimer.Tick += delegate { AdvanceModeTransition(); };
        themeTransitionTimer = new System.Windows.Forms.Timer { Interval = 15 };
        themeTransitionTimer.Tick += delegate { AdvanceThemeTransition(); };
        compactHoverTimer = new System.Windows.Forms.Timer { Interval = 40 };
        compactHoverTimer.Tick += delegate { UpdateCompactHover(); };
        Shown += delegate
        {
            if (!startCompact && !compactMode)
            {
                Rectangle before = Bounds;
                changingMode = true;
                ClientSize = new Size(PanelWidth, PanelHeight);
                Rectangle workArea = Screen.FromRectangle(before).WorkingArea;
                Location = new Point(
                    Math.Max(workArea.Left, Math.Min(workArea.Right - Width, before.Right - Width)),
                    Math.Max(workArea.Top, Math.Min(workArea.Bottom - Height, before.Bottom - Height)));
                changingMode = false;
            }
            UpdateLayoutMode();
            refreshTimer.Start();
            RefreshSnapshot();
        };
        ApplyTheme();
        ApplyLanguage();
        if (startCompact) SetCompactMode(true);
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

    private static Icon LoadApplicationIcon()
    {
        try
        {
            Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon != null) return icon;
        }
        catch { }
        return SystemIcons.Application;
    }

    private void SetTheme(int index)
    {
        int next = Math.Max(0, Math.Min(Themes.Length - 1, index));
        if (next == themeIndex && !themeTransitionTimer.Enabled) return;
        themeFrom = Theme;
        themeIndex = next;
        themeTo = Themes[themeIndex];
        themeTransitionStarted = DateTime.UtcNow;
        themeTransitionTimer.Stop();
        themeTransitionTimer.Start();
        SavePreferences();
    }

    private void AdvanceThemeTransition()
    {
        double raw = Math.Min(1.0, (DateTime.UtcNow - themeTransitionStarted).TotalMilliseconds / TransitionDurationMs);
        float amount = (float)(raw * raw * (3.0 - 2.0 * raw));
        renderedTheme = InterpolateTheme(themeFrom, themeTo, amount);
        ApplyTheme();
        if (raw >= 1.0)
        {
            themeTransitionTimer.Stop();
            renderedTheme = themeTo;
            ApplyTheme();
        }
    }

    private static WidgetTheme InterpolateTheme(WidgetTheme first, WidgetTheme second, float amount)
    {
        return new WidgetTheme(second.Name,
            Blend(first.Background, second.Background, amount),
            Blend(first.Card, second.Card, amount),
            Blend(first.Border, second.Border, amount),
            Blend(first.Text, second.Text, amount),
            Blend(first.Muted, second.Muted, amount),
            Blend(first.SoftText, second.SoftText, amount),
            Blend(first.DimText, second.DimText, amount),
            Blend(first.Accent, second.Accent, amount),
            Blend(first.Track, second.Track, amount),
            Blend(first.Button, second.Button, amount));
    }

    private void SetLanguage(bool useChinese)
    {
        chinese = useChinese;
        ApplyLanguage();
        SavePreferences();
    }

    private void LoadPreferences()
    {
        chinese = String.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
        themeIndex = 0;
        closeBehavior = CloseBehavior.Ask;
        try
        {
            string path = PreferencePath();
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;
                if (String.Equals(parts[0], "language", StringComparison.OrdinalIgnoreCase))
                    chinese = String.Equals(parts[1], "CN", StringComparison.OrdinalIgnoreCase);
                else if (String.Equals(parts[0], "theme", StringComparison.OrdinalIgnoreCase))
                {
                    int parsed;
                    if (Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        themeIndex = Math.Max(0, Math.Min(Themes.Length - 1, parsed));
                }
                else if (String.Equals(parts[0], "close_behavior", StringComparison.OrdinalIgnoreCase))
                {
                    if (String.Equals(parts[1], "minimize", StringComparison.OrdinalIgnoreCase)) closeBehavior = CloseBehavior.Minimize;
                    else if (String.Equals(parts[1], "exit", StringComparison.OrdinalIgnoreCase)) closeBehavior = CloseBehavior.Exit;
                    else closeBehavior = CloseBehavior.Ask;
                }
            }
        }
        catch { }
    }

    private void SavePreferences()
    {
        try
        {
            string path = PreferencePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, new[]
            {
                "language=" + (chinese ? "CN" : "EN"),
                "theme=" + themeIndex.ToString(CultureInfo.InvariantCulture),
                "close_behavior=" + CloseBehaviorValue(closeBehavior)
            });
        }
        catch { }
    }

    private static string PreferencePath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "settings.ini");
    }

    private static string CloseBehaviorValue(CloseBehavior behavior)
    {
        return behavior == CloseBehavior.Minimize ? "minimize" : behavior == CloseBehavior.Exit ? "exit" : "ask";
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        brandDot.ForeColor = Theme.Accent;
        titleLabel.ForeColor = Theme.Text;
        todayTitle.ForeColor = Theme.Muted;
        breakdownTitle.ForeColor = Theme.Muted;
        weeklyTitle.ForeColor = Theme.Muted;
        todayValue.ForeColor = Theme.Text;
        inputValue.ForeColor = Theme.SoftText;
        outputValue.ForeColor = Theme.SoftText;
        cacheValue.ForeColor = Theme.DimText;
        weeklyDetail.ForeColor = Theme.SoftText;
        resetValue.ForeColor = Theme.DimText;
        statusValue.ForeColor = Theme.DimText;
        localOnly.ForeColor = Theme.DimText;
        weeklyCard.BackColor = Theme.Card;
        progressTrack.BackColor = Theme.Track;

        ApplyButtonTheme(pinButton);
        ApplyButtonTheme(closeButton);
        ApplyButtonTheme(themeButton);
        ApplyButtonTheme(expandButton);
        expandButton.FlatAppearance.BorderSize = 0;
        themeButton.ForeColor = Theme.Accent;

        languageToggle.TrackColor = Theme.Button;
        languageToggle.ActiveColor = Theme.Accent;
        languageToggle.InactiveColor = Theme.Muted;
        languageToggle.BorderColor = Theme.Border;
        languageToggle.Invalidate();

        Color usageAccent = GetUsageAccent();
        compactAccent = usageAccent;
        progressFill.BackColor = usageAccent;
        weeklyValue.ForeColor = usageAccent;

        contextMenu.BackColor = Theme.Card;
        contextMenu.ForeColor = Theme.Text;
        ApplyMenuTheme(contextMenu.Items);
        trayMenu.BackColor = Theme.Card;
        trayMenu.ForeColor = Theme.Text;
        ApplyMenuTheme(trayMenu.Items);
        for (int i = 0; i < themeMenuItems.Count; i++) themeMenuItems[i].Checked = i == themeIndex;
        toolTip.SetToolTip(themeButton, "Theme: " + Theme.Name);
        UpdatePinButton();
        weeklyCard.Invalidate();
        Invalidate(true);
    }

    private void ApplyButtonTheme(Button button)
    {
        button.BackColor = Theme.Button;
        button.ForeColor = Theme.Muted;
        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.MouseOverBackColor = Blend(Theme.Button, Theme.Accent, 0.18F);
    }

    private void ApplyMenuTheme(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = Theme.Card;
            item.ForeColor = Theme.Text;
            var menuItem = item as ToolStripMenuItem;
            if (menuItem != null) ApplyMenuTheme(menuItem.DropDownItems);
        }
    }

    private static Color Blend(Color first, Color second, float amount)
    {
        amount = Math.Max(0F, Math.Min(1F, amount));
        return Color.FromArgb(
            (int)Math.Round(first.R + (second.R - first.R) * amount),
            (int)Math.Round(first.G + (second.G - first.G) * amount),
            (int)Math.Round(first.B + (second.B - first.B) * amount));
    }

    private Color GetUsageAccent()
    {
        if (!lastHasWeekly) return Theme.Muted;
        if (lastRemainingPercent > 50) return Theme.Accent;
        if (lastRemainingPercent > 20) return Color.FromArgb(251, 191, 36);
        return Color.FromArgb(251, 113, 133);
    }

    private void ApplyLanguage()
    {
        languageToggle.Chinese = chinese;
        todayTitle.Text = chinese ? "今日 TOKEN" : "TOKENS TODAY";
        breakdownTitle.Text = chinese ? "使用明细" : "BREAKDOWN";
        weeklyTitle.Text = chinese ? "本周剩余" : "WEEKLY LEFT";
        chineseMenuItem.Checked = chinese;
        englishMenuItem.Checked = !chinese;
        UpdatePinButton();
    }

    private void UpdatePinButton()
    {
        pinButton.Text = TopMost ? (chinese ? "置顶" : "PIN") : (chinese ? "自由" : "FREE");
        pinButton.ForeColor = TopMost ? Theme.Muted : Theme.Accent;
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
        SetCompactMode(!compactMode);
    }

    private void MinimizeToBackground()
    {
        if (IsDisposed) return;
        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        trayIcon.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (IsDisposed) return;
        trayIcon.Visible = false;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void HandleCloseRequest()
    {
        if (IsDisposed) return;
        if (closeBehavior == CloseBehavior.Minimize)
        {
            MinimizeToBackground();
            return;
        }
        if (closeBehavior == CloseBehavior.Exit)
        {
            ExitApplication();
            return;
        }

        using (var dialog = new CloseChoiceDialog())
        {
            DialogResult result = dialog.ShowDialog(this);
            if (result != DialogResult.OK) return;
            SetCloseBehavior(dialog.SelectedBehavior);
            if (closeBehavior == CloseBehavior.Minimize) MinimizeToBackground();
            else if (closeBehavior == CloseBehavior.Exit) ExitApplication();
        }
    }

    private void SetCloseBehavior(CloseBehavior behavior)
    {
        closeBehavior = behavior;
        SavePreferences();
        UpdateCloseBehaviorMenus();
    }

    private ToolStripMenuItem BuildCloseBehaviorMenu()
    {
        var menu = new ToolStripMenuItem("关闭行为");
        menu.DropDownItems.Add("点击 × 时询问", null, delegate { SetCloseBehavior(CloseBehavior.Ask); });
        menu.DropDownItems.Add("点击 × 后最小化至后台", null, delegate { SetCloseBehavior(CloseBehavior.Minimize); });
        menu.DropDownItems.Add("点击 × 后彻底结束程序", null, delegate { SetCloseBehavior(CloseBehavior.Exit); });
        menu.DropDownOpening += delegate { UpdateCloseBehaviorMenu(menu); };
        return menu;
    }

    private void UpdateCloseBehaviorMenu(ToolStripMenuItem menu)
    {
        if (menu == null || menu.DropDownItems.Count < 3) return;
        ((ToolStripMenuItem)menu.DropDownItems[0]).Checked = closeBehavior == CloseBehavior.Ask;
        ((ToolStripMenuItem)menu.DropDownItems[1]).Checked = closeBehavior == CloseBehavior.Minimize;
        ((ToolStripMenuItem)menu.DropDownItems[2]).Checked = closeBehavior == CloseBehavior.Exit;
    }

    private void UpdateCloseBehaviorMenus()
    {
        foreach (ToolStripItem item in contextMenu.Items)
        {
            var menu = item as ToolStripMenuItem;
            if (menu != null && menu.Text == "关闭行为") UpdateCloseBehaviorMenu(menu);
        }
        foreach (ToolStripItem item in trayMenu.Items)
        {
            var menu = item as ToolStripMenuItem;
            if (menu != null && menu.Text == "关闭行为") UpdateCloseBehaviorMenu(menu);
        }
    }

    private void ExitApplication()
    {
        if (IsDisposed) return;
        exiting = true;
        trayIcon.Visible = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && !handlingCloseRequest && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            handlingCloseRequest = true;
            try { HandleCloseRequest(); }
            finally { handlingCloseRequest = false; }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && trayIcon != null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SetCompactMode(bool compact)
    {
        if (changingMode || compact == compactMode) return;
        modeTransitionTimer.Stop();
        modeStartBounds = Bounds;
        Size targetSize = compact ? new Size(CompactDiameter, CompactDiameter) : new Size(PanelWidth, PanelHeight);
        Rectangle work = Screen.FromRectangle(modeStartBounds).WorkingArea;
        int targetX = Math.Max(work.Left, Math.Min(work.Right - targetSize.Width, modeStartBounds.Right - targetSize.Width));
        int targetY = Math.Max(work.Top, Math.Min(work.Bottom - targetSize.Height, modeStartBounds.Bottom - targetSize.Height));
        modeTargetBounds = new Rectangle(targetX, targetY, targetSize.Width, targetSize.Height);
        modeTargetCompact = compact;
        modeVisualsSwitched = false;
        modeProgress = 0;
        modeTransitionStarted = DateTime.UtcNow;
        changingMode = true;
        modeTransitionTimer.Start();
    }

    private void AdvanceModeTransition()
    {
        double raw = Math.Min(1.0, (DateTime.UtcNow - modeTransitionStarted).TotalMilliseconds / TransitionDurationMs);
        modeProgress = raw;
        double eased = raw < 0.5 ? 4 * raw * raw * raw : 1 - Math.Pow(-2 * raw + 2, 3) / 2;
        if (!modeVisualsSwitched && raw >= 0.46)
        {
            compactMode = modeTargetCompact;
            modeVisualsSwitched = true;
            UpdateLayoutMode();
        }

        Bounds = InterpolateRectangle(modeStartBounds, modeTargetBounds, eased);
        Opacity = 1.0 - 0.10 * Math.Sin(Math.PI * raw);
        UpdateWindowRegion();
        Invalidate(true);
        if (raw >= 1.0)
        {
            modeTransitionTimer.Stop();
            Bounds = modeTargetBounds;
            compactMode = modeTargetCompact;
            changingMode = false;
            modeProgress = 1;
            Opacity = 1.0;
            UpdateLayoutMode();
        }
    }

    private static Rectangle InterpolateRectangle(Rectangle from, Rectangle to, double amount)
    {
        return new Rectangle(
            (int)Math.Round(from.X + (to.X - from.X) * amount),
            (int)Math.Round(from.Y + (to.Y - from.Y) * amount),
            (int)Math.Round(from.Width + (to.Width - from.Width) * amount),
            (int)Math.Round(from.Height + (to.Height - from.Height) * amount));
    }

    private void UpdateLayoutMode()
    {
        if (detailControls.Count == 0 || expandButton == null) return;
        foreach (Control control in detailControls) control.Visible = !compactMode;
        expandButton.Visible = compactMode;
        int buttonWidth = Math.Max(20, Math.Min(26, ClientSize.Width / 4));
        int buttonHeight = Math.Max(17, Math.Min(20, ClientSize.Height / 5));
        expandButton.Size = new Size(buttonWidth, buttonHeight);
        expandButton.Location = new Point((ClientSize.Width - buttonWidth) / 2, Math.Max(11, ClientSize.Height / 8));

        if (compactMode)
        {
            compactHoverTimer.Start();
        }
        else
        {
            compactHoverTimer.Stop();
            compactHovered = false;
            ApplyPanelLayout();
        }

        closeButton.BringToFront();
        expandButton.BringToFront();

        UpdateWindowRegion();
    }

    private void ApplyPanelLayout()
    {
        bool full = ClientSize.Width >= FullLayoutWidth;
        bool showDetail = full && ClientSize.Height >= DetailLayoutHeight;
        bool showWeekly = ClientSize.Height >= WeeklyLayoutHeight;
        bool showCache = showDetail || (!full && showWeekly);
        bool showStatus = ClientSize.Height >= StatusLayoutHeight;
        languageToggle.Visible = full;
        themeButton.Visible = full;
        pinButton.Visible = full;
        breakdownTitle.Visible = showDetail;
        inputValue.Visible = showDetail;
        outputValue.Visible = showDetail;
        localOnly.Visible = full && showStatus;
        cacheValue.Visible = showCache;
        weeklyCard.Visible = showWeekly;
        statusValue.Visible = showStatus;

        closeButton.Location = new Point(ClientSize.Width - 32, 12);
        titleLabel.Width = full ? 130 : Math.Max(80, closeButton.Left - titleLabel.Left - 5);
        weeklyCard.Location = new Point(16, 135);
        weeklyCard.Size = new Size(Math.Max(1, ClientSize.Width - 32), 108);
        statusValue.Location = new Point(18, Math.Max(0, ClientSize.Height - 29));
        statusValue.Width = Math.Max(120, ClientSize.Width - (full ? 100 : 30));

        if (full)
        {
            languageToggle.Location = new Point(188, 13);
            themeButton.Location = new Point(239, 12);
            pinButton.Location = new Point(283, 12);
            breakdownTitle.Location = new Point(236, 54);
            inputValue.Location = new Point(236, 75);
            outputValue.Location = new Point(236, 93);
            cacheValue.Location = new Point(236, 111);
            cacheValue.Size = new Size(110, 16);
            cacheValue.TextAlign = ContentAlignment.MiddleLeft;
            todayValue.Size = new Size(180, 45);
            localOnly.Location = new Point(ClientSize.Width - 81, Math.Max(0, ClientSize.Height - 29));
        }
        else
        {
            cacheValue.Location = new Point(16, 108);
            cacheValue.Size = new Size(Math.Max(1, ClientSize.Width - 32), 18);
            cacheValue.TextAlign = ContentAlignment.MiddleCenter;
            todayValue.Size = new Size(Math.Max(1, ClientSize.Width - 32), 45);
        }

        int weeklyWidth = weeklyCard.ClientSize.Width;
        weeklyTitle.Location = new Point(13, 12);
        weeklyTitle.Width = full ? 130 : Math.Max(68, weeklyWidth - 86);
        weeklyValue.Location = new Point(Math.Max(76, weeklyWidth - 103), 8);
        weeklyValue.Size = new Size(Math.Min(90, Math.Max(58, weeklyWidth - 89)), 27);
        progressTrack.Location = new Point(13, 43);
        progressTrack.Size = new Size(Math.Max(1, weeklyWidth - 26), 7);
        weeklyDetail.Location = new Point(13, 62);
        weeklyDetail.Width = full ? 170 : Math.Max(1, weeklyWidth - 26);
        resetValue.Visible = full && showWeekly;
        resetValue.Location = new Point(Math.Max(13, weeklyWidth - 139), 62);
        resetValue.Width = 126;
        if (full)
        {
            languageToggle.BringToFront();
            themeButton.BringToFront();
            pinButton.BringToFront();
        }
        weeklyValue.BringToFront();
    }

    private void UpdateCompactHover()
    {
        if (!compactMode || IsDisposed) return;
        bool hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        if (hovered == compactHovered) return;
        compactHovered = hovered;
        Invalidate();
    }

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        GraphicsPath path;
        if (changingMode)
        {
            int radius = CurrentCornerRadius();
            path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), radius);
        }
        else if (compactMode)
        {
            path = new GraphicsPath();
            path.AddEllipse(0, 0, ClientSize.Width, ClientSize.Height);
        }
        else
        {
            path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 12);
        }

        Region oldRegion = Region;
        Region = new Region(path);
        if (oldRegion != null) oldRegion.Dispose();
        path.Dispose();
    }

    private int CurrentCornerRadius()
    {
        int half = Math.Max(2, Math.Min(ClientSize.Width, ClientSize.Height) / 2);
        if (!changingMode) return compactMode ? half : 12;
        double raw = Math.Max(0, Math.Min(1, modeProgress));
        return modeTargetCompact
            ? (int)Math.Round(12 * (1 - raw) + half * raw)
            : (int)Math.Round(half * (1 - raw) + 12 * raw);
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
            lastHasWeekly = snapshot.HasWeekly;

            if (snapshot.HasWeekly)
            {
                weeklyValue.Text = snapshot.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                compactText = weeklyValue.Text;
                lastRemainingPercent = snapshot.RemainingPercent;
                string plan = String.IsNullOrEmpty(snapshot.Plan) ? "CODEX" : snapshot.Plan.ToUpperInvariant();
                weeklyDetail.Text = snapshot.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% used  |  " + plan;
                resetValue.Text = "RESET " + snapshot.ResetAt.ToString("MM-dd HH:mm");
                compactResetText = "RESET " + snapshot.ResetAt.ToString("MM-dd");
                progressFill.Width = Math.Max(0, Math.Min(progressTrack.Width, (int)Math.Round(progressTrack.Width * snapshot.RemainingPercent / 100.0)));
                Color accent = GetUsageAccent();
                progressFill.BackColor = accent;
                weeklyValue.ForeColor = accent;
                compactAccent = accent;
            }
            else
            {
                weeklyValue.Text = "--%";
                compactText = "--%";
                compactAccent = Theme.Muted;
                weeklyDetail.Text = "No weekly sample yet";
                resetValue.Text = "";
                compactResetText = "";
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
        const int WM_SIZING = 0x0214;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        if (m.Msg == WM_ENTERSIZEMOVE && !compactMode)
            compactCollapseRequested = false;
        if (m.Msg == WM_SIZING && compactMode)
        {
            ResizeRect rect = (ResizeRect)Marshal.PtrToStructure(m.LParam, typeof(ResizeRect));
            int proposedWidth = rect.Right - rect.Left;
            int proposedHeight = rect.Bottom - rect.Top;
            int edge = m.WParam.ToInt32();
            bool horizontalEdge = edge == 1 || edge == 2;
            bool verticalEdge = edge == 3 || edge == 6;
            int target = horizontalEdge ? proposedWidth : verticalEdge ? proposedHeight : Math.Max(proposedWidth, proposedHeight);
            float dpiScale = DeviceDpi / 96F;
            int minTarget = (int)Math.Round(CompactDiameter * dpiScale);
            int maxTarget = (int)Math.Round(CompactMaxDiameter * dpiScale);
            target = Math.Max(minTarget, Math.Min(maxTarget, target));

            switch (edge)
            {
                case 1: rect.Left = rect.Right - target; rect.Bottom = rect.Top + target; break;
                case 2: rect.Right = rect.Left + target; rect.Bottom = rect.Top + target; break;
                case 3: rect.Top = rect.Bottom - target; rect.Right = rect.Left + target; break;
                case 4: rect.Left = rect.Right - target; rect.Top = rect.Bottom - target; break;
                case 5: rect.Right = rect.Left + target; rect.Top = rect.Bottom - target; break;
                case 6: rect.Bottom = rect.Top + target; rect.Right = rect.Left + target; break;
                case 7: rect.Left = rect.Right - target; rect.Bottom = rect.Top + target; break;
                case 8: rect.Right = rect.Left + target; rect.Bottom = rect.Top + target; break;
            }
            Marshal.StructureToPtr(rect, m.LParam, false);
        }
        else if (m.Msg == WM_SIZING && !compactMode && !changingMode)
        {
            ResizeRect rect = (ResizeRect)Marshal.PtrToStructure(m.LParam, typeof(ResizeRect));
            int edge = m.WParam.ToInt32();
            int rawWidth = rect.Right - rect.Left;
            int rawHeight = rect.Bottom - rect.Top;
            bool widthAtMinimum = rawWidth <= PanelMinimumWidth || ClientSize.Width <= PanelMinimumWidth + 1;
            bool heightAtMinimum = rawHeight <= PanelMinimumHeight || ClientSize.Height <= PanelMinimumHeight + 1;
            bool pushedPastMinimum = rawWidth <= CompactDragTriggerWidth || rawHeight <= CompactDragTriggerHeight;
            if (widthAtMinimum && heightAtMinimum && pushedPastMinimum) compactCollapseRequested = true;
            int targetWidth = Math.Max(PanelMinimumWidth, Math.Min(PanelWidth, rawWidth));
            int targetHeight = Math.Max(PanelMinimumHeight, Math.Min(PanelHeight, rawHeight));
            bool fromLeft = edge == 1 || edge == 4 || edge == 7;
            bool fromTop = edge == 3 || edge == 4 || edge == 5;
            if (fromLeft) rect.Left = rect.Right - targetWidth; else rect.Right = rect.Left + targetWidth;
            if (fromTop) rect.Top = rect.Bottom - targetHeight; else rect.Bottom = rect.Top + targetHeight;
            Marshal.StructureToPtr(rect, m.LParam, false);
        }
        if (m.Msg == WM_EXITSIZEMOVE && !compactMode && compactCollapseRequested)
        {
            compactCollapseRequested = false;
            BeginInvoke((MethodInvoker)delegate { SetCompactMode(true); });
        }
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
        e.Graphics.Clear(Theme.Background);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (compactMode)
        {
            int inset = Math.Max(5, Math.Min(ClientSize.Width, ClientSize.Height) / 14);
            int diameter = Math.Min(ClientSize.Width, ClientSize.Height) - inset * 2;
            var ringBounds = new Rectangle((ClientSize.Width - diameter) / 2, (ClientSize.Height - diameter) / 2, diameter, diameter);
            int ringWidth = Math.Max(4, diameter / 18);
            Color hoverAccent = compactHovered ? Blend(compactAccent, Theme.Text, 0.22F) : compactAccent;
            Color hoverTrack = compactHovered ? Blend(Theme.Track, Theme.Border, 0.45F) : Theme.Track;
            using (var track = new Pen(hoverTrack, ringWidth))
                e.Graphics.DrawEllipse(track, ringBounds);
            Color accent = hoverAccent;
            using (var progress = new Pen(accent, ringWidth))
            {
                progress.StartCap = LineCap.Round;
                progress.EndCap = LineCap.Round;
                float sweep = (float)Math.Max(0, Math.Min(359.9, 360.0 * lastRemainingPercent / 100.0));
                if (sweep > 0) e.Graphics.DrawArc(progress, ringBounds, -90, sweep);
            }
            float fontSize = Math.Max(15F, Math.Min(28F, Math.Min(ClientSize.Width, ClientSize.Height) * 0.23F));
            RectangleF textBounds = compactHovered
                ? new RectangleF(0, ClientSize.Height * 0.29F, ClientSize.Width, ClientSize.Height * 0.40F)
                : new RectangleF(0, ClientSize.Height * 0.25F, ClientSize.Width, ClientSize.Height * 0.62F);
            using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold))
            using (var brush = new SolidBrush(accent))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                e.Graphics.DrawString(compactText, font, brush, textBounds, format);
            if (compactHovered && !String.IsNullOrEmpty(compactResetText))
            {
                float resetFontSize = Math.Max(5.5F, Math.Min(7.5F, ClientSize.Width * 0.065F));
                RectangleF resetBounds = new RectangleF(4, ClientSize.Height * 0.67F, ClientSize.Width - 8, ClientSize.Height * 0.14F);
                using (var resetFont = new Font("Segoe UI", resetFontSize, FontStyle.Regular))
                using (var resetBrush = new SolidBrush(Blend(Theme.Muted, Theme.Text, 0.18F)))
                using (var resetFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    e.Graphics.DrawString(compactResetText, resetFont, resetBrush, resetBounds, resetFormat);
            }
        }
        else
        {
            using (GraphicsPath borderPath = RoundedPath(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), CurrentCornerRadius()))
            using (var pen = new Pen(Theme.Border))
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
