using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex Usage Widget")]
[assembly: AssemblyDescription("An Apple-inspired local Codex usage widget for Windows")]
[assembly: AssemblyProduct("Codex Usage Widget")]
[assembly: AssemblyVersion("0.6.0.0")]
[assembly: AssemblyFileVersion("0.6.0.0")]

internal sealed class QuotaWindowSnapshot
{
    public bool Available;
    public double UsedPercent;
    public double RemainingPercent;
    public DateTimeOffset ResetAt;
    public bool HasReset;
    public long WindowMinutes;
    public DateTimeOffset SampledAt;
}

internal sealed class UsageSnapshot
{
    public long Input;
    public long Cached;
    public long Output;
    public long Reasoning;
    public long Total;
    public bool Partial;
    public readonly QuotaWindowSnapshot FiveHour = new QuotaWindowSnapshot();
    public readonly QuotaWindowSnapshot Weekly = new QuotaWindowSnapshot();
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
        DateTimeOffset? latestFiveHourStamp = null;
        DateTimeOffset? latestWeeklyStamp = null;
        IDictionary<string, object> latestFiveHour = null;
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

                            foreach (string key in new[] { "primary", "secondary" })
                            {
                                object windowObj;
                                IDictionary<string, object> candidate;
                                if (!rate.TryGetValue(key, out windowObj) || (candidate = Dict(windowObj)) == null) continue;
                                long minutes = LongValue(candidate, "window_minutes");
                                if (minutes >= 240 && minutes <= 360 && (!latestFiveHourStamp.HasValue || stamp > latestFiveHourStamp.Value))
                                {
                                    latestFiveHourStamp = stamp;
                                    latestFiveHour = candidate;
                                    latestPlan = StringValue(rate, "plan_type");
                                }
                                else if (minutes >= 9000 && (!latestWeeklyStamp.HasValue || stamp > latestWeeklyStamp.Value))
                                {
                                    latestWeeklyStamp = stamp;
                                    latestWeekly = candidate;
                                    latestPlan = StringValue(rate, "plan_type");
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        result.Partial = earliest.HasValue && earliest.Value.LocalDateTime > today.AddMinutes(5);
        PopulateQuota(result.FiveHour, latestFiveHour, latestFiveHourStamp);
        PopulateQuota(result.Weekly, latestWeekly, latestWeeklyStamp);
        result.Plan = latestPlan;
        return result;
    }

    private static void PopulateQuota(QuotaWindowSnapshot target, IDictionary<string, object> source, DateTimeOffset? sampledAt)
    {
        if (source == null) return;
        target.Available = true;
        target.WindowMinutes = LongValue(source, "window_minutes");
        target.UsedPercent = Math.Round(Math.Max(0, Math.Min(100, DoubleValue(source, "used_percent"))), 1);
        target.RemainingPercent = Math.Round(Math.Max(0, 100 - target.UsedPercent), 1);
        target.SampledAt = sampledAt ?? DateTimeOffset.MinValue;
        long stamp = LongValue(source, "resets_at");
        if (stamp <= 0) return;
        if (stamp > 100000000000L) stamp /= 1000;
        try
        {
            target.ResetAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(stamp).ToLocalTime();
            target.HasReset = true;
        }
        catch { }
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
    public readonly Color Divider;
    public readonly bool IsDark;

    public WidgetTheme(string name, Color background, Color card, Color border, Color text, Color muted, Color softText, Color dimText, Color accent, Color track, Color button, Color divider, bool isDark)
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
        Divider = divider;
        IsDark = isDark;
    }
}

internal enum AppearanceMode
{
    Auto,
    Light,
    Dark
}

internal sealed class AccentPalette
{
    public readonly string Name;
    public readonly Color Light;
    public readonly Color Dark;

    public AccentPalette(string name, Color light, Color dark)
    {
        Name = name;
        Light = light;
        Dark = dark;
    }
}

internal sealed class RoundedPanel : Panel
{
    public Color SurfaceColor = Color.White;
    public Color OutlineColor = Color.FromArgb(220, 220, 224);
    public Color DividerColor = Color.FromArgb(232, 232, 236);
    public int CornerRadius = 16;
    public int DividerY = -1;

    public RoundedPanel()
    {
        BackColor = Color.White;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = CreateRoundedPath(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), CornerRadius))
        using (var brush = new SolidBrush(SurfaceColor))
            e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = CreateRoundedPath(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), CornerRadius))
        using (var pen = new Pen(OutlineColor))
            e.Graphics.DrawPath(pen, path);
        if (DividerY > 0 && DividerY < Height)
        {
            using (var pen = new Pen(DividerColor))
                e.Graphics.DrawLine(pen, CornerRadius, DividerY, Math.Max(CornerRadius, Width - CornerRadius), DividerY);
        }
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(2, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2));
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
}

internal sealed class WidgetColorTable : ProfessionalColorTable
{
    private readonly WidgetTheme theme;
    private readonly Color selected;

    public WidgetColorTable(WidgetTheme theme)
    {
        this.theme = theme;
        selected = theme.IsDark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(229, 229, 234);
        UseSystemColors = false;
    }

    public override Color ToolStripDropDownBackground { get { return theme.Card; } }
    public override Color ImageMarginGradientBegin { get { return theme.Card; } }
    public override Color ImageMarginGradientMiddle { get { return theme.Card; } }
    public override Color ImageMarginGradientEnd { get { return theme.Card; } }
    public override Color MenuBorder { get { return theme.Border; } }
    public override Color MenuItemBorder { get { return selected; } }
    public override Color MenuItemSelected { get { return selected; } }
    public override Color MenuItemSelectedGradientBegin { get { return selected; } }
    public override Color MenuItemSelectedGradientEnd { get { return selected; } }
    public override Color MenuItemPressedGradientBegin { get { return selected; } }
    public override Color MenuItemPressedGradientMiddle { get { return selected; } }
    public override Color MenuItemPressedGradientEnd { get { return selected; } }
    public override Color SeparatorDark { get { return theme.Divider; } }
    public override Color SeparatorLight { get { return theme.Divider; } }
    public override Color CheckBackground { get { return theme.Button; } }
    public override Color CheckPressedBackground { get { return selected; } }
    public override Color CheckSelectedBackground { get { return selected; } }
}

internal sealed class LanguageToggle : Control
{
    private bool chinese;
    public Color TrackColor = Color.FromArgb(26, 32, 43);
    public Color ActiveColor = Color.FromArgb(74, 222, 128);
    public Color InactiveColor = Color.FromArgb(126, 139, 159);
    public Color BorderColor = Color.FromArgb(48, 57, 73);
    public Color ActiveTextColor = Color.FromArgb(20, 20, 22);
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
        using (var activeBrush = new SolidBrush(ActiveTextColor))
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

internal sealed class SmoothButton : Button
{
    public Color SurfaceColor = Color.FromArgb(237, 237, 240);
    public Color HoverColor = Color.FromArgb(229, 229, 234);
    public Color PressedColor = Color.FromArgb(218, 218, 223);
    private bool hovered;
    private bool pressed;

    public SmoothButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        Color fill = pressed ? PressedColor : hovered ? HoverColor : SurfaceColor;
        RectangleF bounds = new RectangleF(0.6F, 0.6F, Math.Max(1F, Width - 1.2F), Math.Max(1F, Height - 1.2F));
        using (GraphicsPath path = PillPath(bounds))
        using (var brush = new SolidBrush(fill))
            e.Graphics.FillPath(brush, path);

        using (var brush = new SolidBrush(ForeColor))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            e.Graphics.DrawString(Text, Font, brush, new RectangleF(0, 0, Width, Height), format);
    }

    private static GraphicsPath PillPath(RectangleF bounds)
    {
        var path = new GraphicsPath();
        float diameter = Math.Max(2F, Math.Min(bounds.Width, bounds.Height));
        var arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 90, 180);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 180);
        path.CloseFigure();
        return path;
    }
}

internal sealed class CompactExpandButton : Control
{
    public Color SurfaceColor = Color.FromArgb(28, 28, 30);
    public Color MutedColor = Color.FromArgb(142, 142, 147);
    public Color AccentColor = Color.FromArgb(10, 132, 255);
    private float revealAmount;

    public float RevealAmount
    {
        get { return revealAmount; }
        set
        {
            float next = Math.Max(0F, Math.Min(1F, value));
            if (Math.Abs(next - revealAmount) < 0.001F) return;
            revealAmount = next;
            Invalidate();
        }
    }

    public CompactExpandButton()
    {
        Size = new Size(26, 20);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Transparent WinForms painting asks the parent to redraw beneath this hit target.
        base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color quiet = BlendColor(SurfaceColor, MutedColor, 0.40F);
        Color rendered = BlendColor(quiet, AccentColor, 0.05F + 0.55F * revealAmount);
        float centerX = Width / 2F;
        float centerY = Height / 2F + 0.5F;
        float halfWidth = Math.Max(2.5F, Math.Min(3.8F, Width * 0.14F));
        float rise = Math.Max(1.7F, Math.Min(2.6F, Height * 0.13F));
        using (var pen = new Pen(rendered, 1.20F + 0.25F * revealAmount))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            e.Graphics.DrawLine(pen, centerX - halfWidth, centerY + rise / 2F, centerX, centerY - rise / 2F);
            e.Graphics.DrawLine(pen, centerX, centerY - rise / 2F, centerX + halfWidth, centerY + rise / 2F);
        }
    }

    private static Color BlendColor(Color from, Color to, float amount)
    {
        amount = Math.Max(0F, Math.Min(1F, amount));
        return Color.FromArgb(
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
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

    public CloseChoiceDialog(WidgetTheme theme)
    {
        Text = "关闭方式";
        ClientSize = new Size(390, 142);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI Variable Text", 9F);
        BackColor = theme.Background;
        ForeColor = theme.Text;

        var title = new Label { Text = "点击右上角 × 时，你希望如何处理程序？", Location = new Point(18, 16), Size = new Size(350, 24), ForeColor = ForeColor };
        var hint = new Label { Text = "选择后会记住，也可从右键菜单修改。", Location = new Point(18, 43), Size = new Size(350, 20), ForeColor = theme.Muted };
        var minimize = new Button { Text = "最小化至后台", Location = new Point(18, 86), Size = new Size(122, 30), DialogResult = DialogResult.OK };
        var exit = new Button { Text = "彻底结束程序", Location = new Point(148, 86), Size = new Size(122, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Location = new Point(278, 86), Size = new Size(78, 30), DialogResult = DialogResult.Cancel };
        minimize.Click += delegate { SelectedBehavior = CloseBehavior.Minimize; };
        exit.Click += delegate { SelectedBehavior = CloseBehavior.Exit; };
        StyleButton(minimize, theme, true);
        StyleButton(exit, theme, false);
        StyleButton(cancel, theme, false);
        Controls.Add(title); Controls.Add(hint); Controls.Add(minimize); Controls.Add(exit); Controls.Add(cancel);
        CancelButton = cancel;
    }

    private static void StyleButton(Button button, WidgetTheme theme, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = primary ? theme.Accent : theme.Button;
        button.ForeColor = primary ? (theme.IsDark ? Color.FromArgb(20, 20, 22) : Color.White) : theme.Text;
        button.Cursor = Cursors.Hand;
        using (var path = new GraphicsPath())
        {
            int diameter = button.Height;
            path.AddArc(0, 0, diameter, diameter, 90, 180);
            path.AddArc(button.Width - diameter, 0, diameter, diameter, 270, 180);
            path.CloseFigure();
            button.Region = new Region(path);
        }
    }
}

internal sealed class WidgetForm : Form
{
    private readonly Label brandDot;
    private readonly Label titleLabel;
    private readonly Label todayTitle;
    private readonly Label breakdownTitle;
    private readonly Label fiveHourTitle;
    private readonly Label weeklyTitle;
    private readonly Label todayValue;
    private readonly Label inputValue;
    private readonly Label outputValue;
    private readonly Label cacheValue;
    private readonly Label fiveHourValue;
    private readonly Label weeklyValue;
    private readonly Label fiveHourDetail;
    private readonly Label weeklyDetail;
    private readonly Label fiveHourResetValue;
    private readonly Label resetValue;
    private readonly Label statusValue;
    private readonly Label localOnly;
    private readonly RoundedPanel weeklyCard;
    private readonly Panel fiveHourProgressFill;
    private readonly Panel fiveHourProgressTrack;
    private readonly Panel progressFill;
    private readonly Panel progressTrack;
    private readonly Button pinButton;
    private readonly Button closeButton;
    private readonly Button themeButton;
    private readonly CompactExpandButton expandButton;
    private readonly LanguageToggle languageToggle;
    private readonly ContextMenuStrip contextMenu;
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip trayMenu;
    private readonly List<ToolStripMenuItem> themeMenuItems = new List<ToolStripMenuItem>();
    private readonly List<ToolStripMenuItem> appearanceMenuItems = new List<ToolStripMenuItem>();
    private ToolStripMenuItem chineseMenuItem;
    private ToolStripMenuItem englishMenuItem;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer modeTransitionTimer;
    private readonly System.Windows.Forms.Timer themeTransitionTimer;
    private readonly System.Windows.Forms.Timer compactHoverTimer;
    private readonly System.Windows.Forms.Timer valueAnimationTimer;
    private readonly ToolTip toolTip;
    private readonly List<Control> detailControls = new List<Control>();
    private bool refreshing;
    private double lastRemainingPercent;
    private double lastFiveHourRemainingPercent;
    private double lastWeeklyRemainingPercent;
    private bool compactMode;
    private bool changingMode;
    private bool chinese;
    private bool lastHasFiveHour;
    private bool lastHasWeekly;
    private int themeIndex;
    private AppearanceMode appearanceMode;
    private bool resolvedDarkMode;
    private bool motionEnabled;
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
    private float compactHoverAmount;
    private DateTime valueAnimationStarted;
    private double valueAnimationFromFiveHour;
    private double valueAnimationFromWeekly;
    private double animatedFiveHourRemaining;
    private double animatedWeeklyRemaining;

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
    private const int ModeTransitionDurationMs = 210;
    private const int ThemeTransitionDurationMs = 240;
    private const int ValueTransitionDurationMs = 260;
    private const int PanelCornerRadius = 20;
    private const string UiFontName = "Segoe UI Variable Text";

    private static readonly AccentPalette[] AccentPalettes =
    {
        new AccentPalette("Apple Green", Color.FromArgb(52, 199, 89), Color.FromArgb(48, 209, 88)),
        new AccentPalette("California Blue", Color.FromArgb(0, 122, 255), Color.FromArgb(10, 132, 255)),
        new AccentPalette("Orchid Purple", Color.FromArgb(175, 82, 222), Color.FromArgb(191, 90, 242)),
        new AccentPalette("Watermelon Pink", Color.FromArgb(255, 45, 85), Color.FromArgb(255, 55, 95)),
        new AccentPalette("Sunset Orange", Color.FromArgb(255, 149, 0), Color.FromArgb(255, 159, 10))
    };

    private WidgetTheme Theme { get { return renderedTheme ?? BuildTheme(themeIndex, resolvedDarkMode); } }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint parameter, ref bool value, uint update);

    private static WidgetTheme BuildTheme(int accentIndex, bool dark)
    {
        int safeIndex = Math.Max(0, Math.Min(AccentPalettes.Length - 1, accentIndex));
        AccentPalette palette = AccentPalettes[safeIndex];
        Color accent = dark ? palette.Dark : palette.Light;
        if (dark)
        {
            return new WidgetTheme(
                "Dark · " + palette.Name,
                Color.FromArgb(28, 28, 30), Color.FromArgb(44, 44, 46), Color.FromArgb(61, 61, 64),
                Color.FromArgb(245, 245, 247), Color.FromArgb(174, 174, 178), Color.FromArgb(209, 209, 214),
                Color.FromArgb(126, 126, 132), accent, Color.FromArgb(60, 60, 64), Color.FromArgb(48, 48, 51),
                Color.FromArgb(58, 58, 61), true);
        }
        return new WidgetTheme(
            "Light · " + palette.Name,
            Color.FromArgb(239, 240, 243), Color.FromArgb(248, 248, 250), Color.FromArgb(209, 211, 216),
            Color.FromArgb(31, 31, 33), Color.FromArgb(104, 104, 108), Color.FromArgb(73, 73, 76),
            Color.FromArgb(145, 145, 150), accent, Color.FromArgb(218, 220, 225), Color.FromArgb(226, 228, 232),
            Color.FromArgb(224, 225, 229), false);
    }

    private static bool SystemPrefersDarkMode()
    {
        try
        {
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0;
        }
        catch { return false; }
    }

    private static bool ClientAnimationsEnabled()
    {
        bool enabled = true;
        try { SystemParametersInfo(0x1042, 0, ref enabled, 0); }
        catch { }
        return enabled;
    }

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
        resolvedDarkMode = appearanceMode == AppearanceMode.Dark || (appearanceMode == AppearanceMode.Auto && SystemPrefersDarkMode());
        motionEnabled = ClientAnimationsEnabled();
        renderedTheme = BuildTheme(themeIndex, resolvedDarkMode);
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
        Font = new Font(UiFontName, 9F);

        var work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - Width - 20, work.Bottom - Height - 20);

        brandDot = MakeLabel("●", 18, 13, 15, 20, 9F, Theme.Accent, FontStyle.Bold);
        titleLabel = MakeLabel("CODEX METER", 37, 14, 130, 22, 10F, Theme.Text, FontStyle.Bold);
        Controls.Add(brandDot);
        Controls.Add(titleLabel);

        pinButton = MakeButton("PIN", 282, 11, 40, 27);
        closeButton = MakeButton("×", 327, 11, 27, 27);
        closeButton.Font = new Font(UiFontName, 12F, FontStyle.Regular);
        pinButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(pinButton);
        Controls.Add(closeButton);
        pinButton.Click += delegate { TopMost = !TopMost; UpdatePinButton(); };
        closeButton.Click += delegate { HandleCloseRequest(); };
        toolTip = new ToolTip();
        toolTip.SetToolTip(closeButton, "关闭行为");

        languageToggle = new LanguageToggle { Location = new Point(187, 14), Anchor = AnchorStyles.Top | AnchorStyles.Right, Chinese = chinese };
        languageToggle.ValueChanged += delegate { SetLanguage(languageToggle.Chinese); };
        Controls.Add(languageToggle);

        themeButton = MakeButton("◐", 238, 11, 36, 27);
        themeButton.Font = new Font(UiFontName, 10F, FontStyle.Regular);
        themeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        themeButton.Click += delegate { CycleAppearanceMode(); };
        Controls.Add(themeButton);

        todayTitle = MakeLabel("TOKENS TODAY", 20, 55, 140, 18, 8.4F, Theme.Muted, FontStyle.Bold);
        Controls.Add(todayTitle);
        todayValue = MakeLabel("--", 18, 70, 180, 45, 27F, Theme.Text, FontStyle.Bold);
        Controls.Add(todayValue);

        breakdownTitle = MakeLabel("BREAKDOWN", 236, 54, 105, 18, 8.5F, Theme.Muted, FontStyle.Bold);
        breakdownTitle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(breakdownTitle);
        inputValue = MakeLabel("IN   --", 236, 75, 110, 17, 9.5F, Theme.SoftText, FontStyle.Regular);
        outputValue = MakeLabel("OUT  --", 236, 93, 110, 17, 9.5F, Theme.SoftText, FontStyle.Regular);
        cacheValue = MakeLabel("CACHE --", 236, 111, 110, 16, 8.5F, Theme.DimText, FontStyle.Regular);
        inputValue.Anchor = outputValue.Anchor = cacheValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(inputValue); Controls.Add(outputValue); Controls.Add(cacheValue);

        weeklyCard = new RoundedPanel { Location = new Point(16, 135), Size = new Size(328, 108), SurfaceColor = Theme.Card, OutlineColor = Theme.Border, DividerColor = Theme.Divider, CornerRadius = 15, DividerY = 54, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(weeklyCard);
        fiveHourTitle = MakeLabel("5-HOUR LEFT", 13, 6, 130, 16, 7.8F, Theme.Muted, FontStyle.Bold);
        fiveHourValue = MakeLabel("--%", 222, 3, 90, 23, 14F, Theme.Accent, FontStyle.Bold);
        fiveHourValue.TextAlign = ContentAlignment.MiddleRight;
        fiveHourValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        weeklyCard.Controls.Add(fiveHourTitle);
        weeklyCard.Controls.Add(fiveHourValue);

        fiveHourProgressTrack = new Panel { Location = new Point(13, 27), Size = new Size(302, 6), BackColor = Theme.Track, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        fiveHourProgressFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 6), BackColor = Theme.Accent };
        fiveHourProgressTrack.Controls.Add(fiveHourProgressFill);
        weeklyCard.Controls.Add(fiveHourProgressTrack);
        fiveHourDetail = MakeLabel("Waiting for local data", 13, 35, 170, 15, 7.5F, Theme.SoftText, FontStyle.Regular);
        fiveHourResetValue = MakeLabel("", 189, 35, 126, 15, 7.5F, Theme.DimText, FontStyle.Regular);
        fiveHourResetValue.TextAlign = ContentAlignment.MiddleRight;
        fiveHourResetValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        weeklyCard.Controls.Add(fiveHourDetail); weeklyCard.Controls.Add(fiveHourResetValue);

        weeklyTitle = MakeLabel("WEEKLY LEFT", 13, 56, 130, 16, 7.8F, Theme.Muted, FontStyle.Bold);
        weeklyCard.Controls.Add(weeklyTitle);
        weeklyValue = MakeLabel("--%", 222, 53, 90, 23, 14F, Theme.Accent, FontStyle.Bold);
        weeklyValue.TextAlign = ContentAlignment.MiddleRight;
        weeklyValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        weeklyCard.Controls.Add(weeklyValue);

        progressTrack = new Panel { Location = new Point(13, 77), Size = new Size(302, 6), BackColor = Theme.Track, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        progressFill = new Panel { Location = new Point(0, 0), Size = new Size(0, 6), BackColor = Theme.Accent };
        progressTrack.Controls.Add(progressFill);
        weeklyCard.Controls.Add(progressTrack);
        weeklyDetail = MakeLabel("Waiting for local data", 13, 85, 170, 15, 7.5F, Theme.SoftText, FontStyle.Regular);
        resetValue = MakeLabel("", 189, 85, 126, 15, 7.5F, Theme.DimText, FontStyle.Regular);
        resetValue.TextAlign = ContentAlignment.MiddleRight;
        resetValue.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        weeklyCard.Controls.Add(weeklyDetail); weeklyCard.Controls.Add(resetValue);

        statusValue = MakeLabel("Starting...", 18, 257, 260, 18, 8F, Theme.DimText, FontStyle.Regular);
        statusValue.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(statusValue);
        localOnly = MakeLabel("LOCAL ONLY", 267, 257, 77, 18, 7.2F, Theme.DimText, FontStyle.Bold);
        localOnly.TextAlign = ContentAlignment.MiddleRight;
        localOnly.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Controls.Add(localOnly);

        foreach (Control control in Controls) detailControls.Add(control);

        expandButton = new CompactExpandButton();
        expandButton.Visible = false;
        expandButton.Click += delegate { SetCompactMode(false); };
        Controls.Add(expandButton);

        toolTip.SetToolTip(expandButton, "Back to full panel");
        MouseDown += DragWindow;
        DoubleClick += ToggleSizeMode;
        foreach (Control control in Controls)
            if (!(control is Button) && control != languageToggle && control != expandButton)
                control.MouseDown += DragWindow;

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

        var appearanceMenu = new ToolStripMenuItem("外观 / Appearance");
        var autoAppearance = new ToolStripMenuItem("自动（跟随系统）", null, delegate { SetAppearanceMode(AppearanceMode.Auto); });
        var lightAppearance = new ToolStripMenuItem("浅色", null, delegate { SetAppearanceMode(AppearanceMode.Light); });
        var darkAppearance = new ToolStripMenuItem("深色", null, delegate { SetAppearanceMode(AppearanceMode.Dark); });
        appearanceMenuItems.Add(autoAppearance);
        appearanceMenuItems.Add(lightAppearance);
        appearanceMenuItems.Add(darkAppearance);
        appearanceMenu.DropDownItems.Add(autoAppearance);
        appearanceMenu.DropDownItems.Add(lightAppearance);
        appearanceMenu.DropDownItems.Add(darkAppearance);
        contextMenu.Items.Add(appearanceMenu);

        var themeMenu = new ToolStripMenuItem("强调色 / Accent");
        for (int i = 0; i < AccentPalettes.Length; i++)
        {
            int selectedTheme = i;
            var item = new ToolStripMenuItem(AccentPalettes[i].Name, null, delegate { SetTheme(selectedTheme); });
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
        compactHoverTimer = new System.Windows.Forms.Timer { Interval = 15 };
        compactHoverTimer.Tick += delegate { UpdateCompactHover(); };
        valueAnimationTimer = new System.Windows.Forms.Timer { Interval = 15 };
        valueAnimationTimer.Tick += delegate { AdvanceValueAnimation(); };
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
        return new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), ForeColor = color, BackColor = Color.Transparent, Font = new Font(UiFontName, size, style), AutoEllipsis = true };
    }

    private static Button MakeButton(string text, int x, int y, int width, int height)
    {
        var button = new SmoothButton { Text = text, Location = new Point(x, y), Size = new Size(width, height), ForeColor = Color.FromArgb(99, 99, 102), Font = new Font(UiFontName, 7.5F, FontStyle.Bold) };
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
        int next = Math.Max(0, Math.Min(AccentPalettes.Length - 1, index));
        if (next == themeIndex && !themeTransitionTimer.Enabled) return;
        themeIndex = next;
        BeginThemeTransition(BuildTheme(themeIndex, resolvedDarkMode));
        SavePreferences();
    }

    private void SetAppearanceMode(AppearanceMode mode)
    {
        appearanceMode = mode;
        bool nextDark = mode == AppearanceMode.Dark || (mode == AppearanceMode.Auto && SystemPrefersDarkMode());
        resolvedDarkMode = nextDark;
        BeginThemeTransition(BuildTheme(themeIndex, resolvedDarkMode));
        SavePreferences();
    }

    private void CycleAppearanceMode()
    {
        if (appearanceMode == AppearanceMode.Auto) SetAppearanceMode(AppearanceMode.Light);
        else if (appearanceMode == AppearanceMode.Light) SetAppearanceMode(AppearanceMode.Dark);
        else SetAppearanceMode(AppearanceMode.Auto);
    }

    private void RefreshAutomaticAppearance()
    {
        if (appearanceMode != AppearanceMode.Auto) return;
        bool nextDark = SystemPrefersDarkMode();
        if (nextDark == resolvedDarkMode) return;
        resolvedDarkMode = nextDark;
        BeginThemeTransition(BuildTheme(themeIndex, resolvedDarkMode));
    }

    private void BeginThemeTransition(WidgetTheme target)
    {
        themeFrom = Theme;
        themeTo = target;
        themeTransitionStarted = DateTime.UtcNow;
        themeTransitionTimer.Stop();
        if (!motionEnabled)
        {
            renderedTheme = themeTo;
            ApplyTheme();
            return;
        }
        themeTransitionTimer.Start();
    }

    private void AdvanceThemeTransition()
    {
        double raw = Math.Min(1.0, (DateTime.UtcNow - themeTransitionStarted).TotalMilliseconds / ThemeTransitionDurationMs);
        float amount = (float)(1.0 - Math.Pow(1.0 - raw, 3.0));
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
            Blend(first.Button, second.Button, amount),
            Blend(first.Divider, second.Divider, amount),
            second.IsDark);
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
        appearanceMode = AppearanceMode.Auto;
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
                        themeIndex = Math.Max(0, Math.Min(AccentPalettes.Length - 1, parsed));
                }
                else if (String.Equals(parts[0], "appearance", StringComparison.OrdinalIgnoreCase))
                {
                    if (String.Equals(parts[1], "light", StringComparison.OrdinalIgnoreCase)) appearanceMode = AppearanceMode.Light;
                    else if (String.Equals(parts[1], "dark", StringComparison.OrdinalIgnoreCase)) appearanceMode = AppearanceMode.Dark;
                    else appearanceMode = AppearanceMode.Auto;
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
                "appearance=" + AppearanceModeValue(appearanceMode),
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

    private static string AppearanceModeValue(AppearanceMode mode)
    {
        return mode == AppearanceMode.Light ? "light" : mode == AppearanceMode.Dark ? "dark" : "auto";
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        brandDot.ForeColor = Theme.Accent;
        titleLabel.ForeColor = Theme.Text;
        todayTitle.ForeColor = Theme.Muted;
        breakdownTitle.ForeColor = Theme.Muted;
        fiveHourTitle.ForeColor = Theme.Muted;
        weeklyTitle.ForeColor = Theme.Muted;
        todayValue.ForeColor = Theme.Text;
        inputValue.ForeColor = Theme.SoftText;
        outputValue.ForeColor = Theme.SoftText;
        cacheValue.ForeColor = Theme.DimText;
        fiveHourDetail.ForeColor = Theme.SoftText;
        weeklyDetail.ForeColor = Theme.SoftText;
        fiveHourResetValue.ForeColor = Theme.DimText;
        resetValue.ForeColor = Theme.DimText;
        statusValue.ForeColor = Theme.DimText;
        localOnly.ForeColor = Theme.DimText;
        weeklyCard.SurfaceColor = Theme.Card;
        weeklyCard.BackColor = Theme.Background;
        weeklyCard.OutlineColor = Theme.Border;
        weeklyCard.DividerColor = Theme.Divider;
        fiveHourProgressTrack.BackColor = Theme.Track;
        progressTrack.BackColor = Theme.Track;
        UpdatePillRegion(fiveHourProgressTrack);
        UpdatePillRegion(fiveHourProgressFill);
        UpdatePillRegion(progressTrack);
        UpdatePillRegion(progressFill);

        ApplyButtonTheme(pinButton);
        ApplyButtonTheme(closeButton);
        ApplyButtonTheme(themeButton);
        expandButton.SurfaceColor = Theme.Background;
        expandButton.MutedColor = Theme.Muted;
        expandButton.AccentColor = Theme.Accent;
        expandButton.Invalidate();
        themeButton.ForeColor = Theme.Accent;

        languageToggle.TrackColor = Theme.Button;
        languageToggle.ActiveColor = Theme.Accent;
        languageToggle.InactiveColor = Theme.Muted;
        languageToggle.BorderColor = Theme.Border;
        languageToggle.ActiveTextColor = Theme.IsDark ? Color.FromArgb(20, 20, 22) : Color.White;
        languageToggle.Invalidate();

        Color usageAccent = GetUsageAccent();
        compactAccent = usageAccent;
        Color fiveHourAccent = GetQuotaAccent(lastHasFiveHour, lastFiveHourRemainingPercent);
        Color weeklyAccent = GetQuotaAccent(lastHasWeekly, lastWeeklyRemainingPercent);
        fiveHourProgressFill.BackColor = fiveHourAccent;
        fiveHourValue.ForeColor = fiveHourAccent;
        progressFill.BackColor = weeklyAccent;
        weeklyValue.ForeColor = weeklyAccent;

        contextMenu.BackColor = Theme.Card;
        contextMenu.ForeColor = Theme.Text;
        contextMenu.Renderer = new ToolStripProfessionalRenderer(new WidgetColorTable(Theme));
        ApplyMenuTheme(contextMenu.Items);
        trayMenu.BackColor = Theme.Card;
        trayMenu.ForeColor = Theme.Text;
        trayMenu.Renderer = new ToolStripProfessionalRenderer(new WidgetColorTable(Theme));
        ApplyMenuTheme(trayMenu.Items);
        for (int i = 0; i < themeMenuItems.Count; i++) themeMenuItems[i].Checked = i == themeIndex;
        for (int i = 0; i < appearanceMenuItems.Count; i++) appearanceMenuItems[i].Checked = i == (int)appearanceMode;
        themeButton.Text = appearanceMode == AppearanceMode.Light ? "☀" : appearanceMode == AppearanceMode.Dark ? "☾" : "◐";
        toolTip.SetToolTip(themeButton, (chinese ? "外观：" : "Appearance: ") + AppearanceDisplayName());
        UpdatePinButton();
        weeklyCard.Invalidate();
        Invalidate(true);
    }

    private void ApplyButtonTheme(Button button)
    {
        button.ForeColor = Theme.Muted;
        SmoothButton smooth = button as SmoothButton;
        if (smooth != null)
        {
            smooth.BackColor = Theme.Background;
            smooth.SurfaceColor = Theme.Button;
            smooth.HoverColor = Blend(Theme.Button, Theme.Accent, Theme.IsDark ? 0.18F : 0.10F);
            smooth.PressedColor = Blend(Theme.Button, Theme.Accent, Theme.IsDark ? 0.28F : 0.18F);
            Region old = smooth.Region;
            smooth.Region = null;
            if (old != null) old.Dispose();
            smooth.Invalidate();
            return;
        }

        button.BackColor = Theme.Button;
        button.FlatAppearance.BorderSize = 0;
    }

    private string AppearanceDisplayName()
    {
        if (appearanceMode == AppearanceMode.Light) return chinese ? "浅色" : "Light";
        if (appearanceMode == AppearanceMode.Dark) return chinese ? "深色" : "Dark";
        return chinese ? "自动" : "Auto";
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
        return GetQuotaAccent(lastHasFiveHour || lastHasWeekly, lastRemainingPercent);
    }

    private Color GetQuotaAccent(bool available, double remainingPercent)
    {
        if (!available) return Theme.Muted;
        if (remainingPercent > 50) return Theme.Accent;
        if (remainingPercent > 20) return Theme.IsDark ? Color.FromArgb(255, 214, 10) : Color.FromArgb(255, 149, 0);
        return Theme.IsDark ? Color.FromArgb(255, 69, 58) : Color.FromArgb(255, 59, 48);
    }

    private void ApplyLanguage()
    {
        languageToggle.Chinese = chinese;
        todayTitle.Text = chinese ? "今日 TOKEN" : "TOKENS TODAY";
        breakdownTitle.Text = chinese ? "使用明细" : "BREAKDOWN";
        fiveHourTitle.Text = chinese ? "5小时剩余" : "5-HOUR LEFT";
        weeklyTitle.Text = chinese ? "本周剩余" : "WEEKLY LEFT";
        chineseMenuItem.Checked = chinese;
        englishMenuItem.Checked = !chinese;
        toolTip.SetToolTip(themeButton, (chinese ? "外观：" : "Appearance: ") + AppearanceDisplayName());
        UpdatePinButton();
    }

    private void UpdatePinButton()
    {
        pinButton.Text = TopMost ? (chinese ? "置顶" : "PIN") : (chinese ? "自由" : "FREE");
        pinButton.ForeColor = TopMost ? Theme.Accent : Theme.Muted;
        toolTip.SetToolTip(pinButton, TopMost ? (chinese ? "已置顶" : "Pinned") : (chinese ? "未置顶" : "Not pinned"));
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

        using (var dialog = new CloseChoiceDialog(Theme))
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
        if (!motionEnabled)
        {
            Bounds = modeTargetBounds;
            compactMode = modeTargetCompact;
            changingMode = false;
            modeProgress = 1;
            Opacity = 1.0;
            UpdateLayoutMode();
            return;
        }
        modeTransitionTimer.Start();
    }

    private void AdvanceModeTransition()
    {
        double raw = Math.Min(1.0, (DateTime.UtcNow - modeTransitionStarted).TotalMilliseconds / ModeTransitionDurationMs);
        modeProgress = raw;
        double eased = 1.0 - Math.Pow(1.0 - raw, 4.0);
        if (!modeVisualsSwitched && raw >= 0.46)
        {
            compactMode = modeTargetCompact;
            modeVisualsSwitched = true;
            UpdateLayoutMode();
        }

        Bounds = InterpolateRectangle(modeStartBounds, modeTargetBounds, eased);
        Opacity = 1.0 - 0.06 * Math.Sin(Math.PI * raw);
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
        int buttonWidth = Math.Max(22, Math.Min(28, ClientSize.Width / 4));
        int buttonHeight = Math.Max(18, Math.Min(21, ClientSize.Height / 5));
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
            compactHoverAmount = 0F;
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
            localOnly.Location = new Point(ClientSize.Width - 93, Math.Max(0, ClientSize.Height - 29));
        }
        else
        {
            cacheValue.Location = new Point(16, 108);
            cacheValue.Size = new Size(Math.Max(1, ClientSize.Width - 32), 18);
            cacheValue.TextAlign = ContentAlignment.MiddleCenter;
            todayValue.Size = new Size(Math.Max(1, ClientSize.Width - 32), 45);
        }

        int weeklyWidth = weeklyCard.ClientSize.Width;
        fiveHourTitle.Location = new Point(13, 6);
        fiveHourTitle.Width = full ? 130 : Math.Max(68, weeklyWidth - 86);
        fiveHourValue.Location = new Point(Math.Max(76, weeklyWidth - 103), 3);
        fiveHourValue.Size = new Size(Math.Min(90, Math.Max(58, weeklyWidth - 89)), 23);
        fiveHourProgressTrack.Location = new Point(13, 27);
        fiveHourProgressTrack.Size = new Size(Math.Max(1, weeklyWidth - 26), 6);
        fiveHourDetail.Location = new Point(13, 35);
        fiveHourDetail.Width = full ? 170 : Math.Max(1, weeklyWidth - 26);
        fiveHourResetValue.Visible = full && showWeekly;
        fiveHourResetValue.Location = new Point(Math.Max(13, weeklyWidth - 139), 35);
        fiveHourResetValue.Width = 126;

        weeklyTitle.Location = new Point(13, 56);
        weeklyTitle.Width = full ? 130 : Math.Max(68, weeklyWidth - 86);
        weeklyValue.Location = new Point(Math.Max(76, weeklyWidth - 103), 53);
        weeklyValue.Size = new Size(Math.Min(90, Math.Max(58, weeklyWidth - 89)), 23);
        progressTrack.Location = new Point(13, 77);
        progressTrack.Size = new Size(Math.Max(1, weeklyWidth - 26), 6);
        weeklyDetail.Location = new Point(13, 85);
        weeklyDetail.Width = full ? 170 : Math.Max(1, weeklyWidth - 26);
        resetValue.Visible = full && showWeekly;
        resetValue.Location = new Point(Math.Max(13, weeklyWidth - 139), 85);
        resetValue.Width = 126;
        if (full)
        {
            languageToggle.BringToFront();
            themeButton.BringToFront();
            pinButton.BringToFront();
        }
        fiveHourValue.BringToFront();
        weeklyValue.BringToFront();
        UpdatePillRegion(fiveHourProgressTrack);
        UpdatePillRegion(fiveHourProgressFill);
        UpdatePillRegion(progressTrack);
        UpdatePillRegion(progressFill);
    }

    private void UpdateCompactHover()
    {
        if (!compactMode || IsDisposed) return;
        bool hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        compactHovered = hovered;
        float target = hovered ? 1F : 0F;
        float step = motionEnabled ? 0.12F : 1F;
        float before = compactHoverAmount;
        if (compactHoverAmount < target) compactHoverAmount = Math.Min(target, compactHoverAmount + step);
        else if (compactHoverAmount > target) compactHoverAmount = Math.Max(target, compactHoverAmount - step);
        expandButton.RevealAmount = compactHoverAmount;
        if (Math.Abs(compactHoverAmount - before) > 0.001F) Invalidate();
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
            path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), PanelCornerRadius);
        }

        Region oldRegion = Region;
        Region = new Region(path);
        if (oldRegion != null) oldRegion.Dispose();
        path.Dispose();
    }

    private int CurrentCornerRadius()
    {
        int half = Math.Max(2, Math.Min(ClientSize.Width, ClientSize.Height) / 2);
        if (!changingMode) return compactMode ? half : PanelCornerRadius;
        double raw = Math.Max(0, Math.Min(1, modeProgress));
        return modeTargetCompact
            ? (int)Math.Round(PanelCornerRadius * (1 - raw) + half * raw)
            : (int)Math.Round(half * (1 - raw) + PanelCornerRadius * raw);
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

    private static void UpdatePillRegion(Control control)
    {
        if (control == null || control.Width <= 1 || control.Height <= 1) return;
        using (GraphicsPath path = RoundedPath(new Rectangle(0, 0, control.Width, control.Height), Math.Max(2, control.Height / 2)))
        {
            Region old = control.Region;
            control.Region = new Region(path);
            if (old != null) old.Dispose();
        }
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
        RefreshAutomaticAppearance();
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
            lastHasFiveHour = snapshot.FiveHour.Available;
            lastHasWeekly = snapshot.Weekly.Available;
            lastFiveHourRemainingPercent = snapshot.FiveHour.RemainingPercent;
            lastWeeklyRemainingPercent = snapshot.Weekly.RemainingPercent;

            string plan = String.IsNullOrEmpty(snapshot.Plan) ? "CODEX" : snapshot.Plan.ToUpperInvariant();
            ApplyQuotaWindow(snapshot.FiveHour, fiveHourValue, fiveHourDetail, fiveHourResetValue, fiveHourProgressTrack, fiveHourProgressFill, false, plan);
            ApplyQuotaWindow(snapshot.Weekly, weeklyValue, weeklyDetail, resetValue, progressTrack, progressFill, true, plan);
            BeginValueAnimation();

            if (lastHasFiveHour && lastHasWeekly) lastRemainingPercent = Math.Min(lastFiveHourRemainingPercent, lastWeeklyRemainingPercent);
            else if (lastHasFiveHour) lastRemainingPercent = lastFiveHourRemainingPercent;
            else if (lastHasWeekly) lastRemainingPercent = lastWeeklyRemainingPercent;
            else lastRemainingPercent = 0;
            compactText = (lastHasFiveHour || lastHasWeekly)
                ? lastRemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : "--%";
            var compactLines = new List<string>();
            var compactToolTipLines = new List<string>();
            if (lastHasFiveHour)
            {
                compactLines.Add("5H " + (snapshot.FiveHour.HasReset ? snapshot.FiveHour.ResetAt.ToString("MM-dd") : "--"));
                compactToolTipLines.Add("5H " + snapshot.FiveHour.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + (snapshot.FiveHour.HasReset ? " | RESET " + snapshot.FiveHour.ResetAt.ToString("MM-dd HH:mm") : ""));
            }
            if (lastHasWeekly)
            {
                compactLines.Add("W  " + (snapshot.Weekly.HasReset ? snapshot.Weekly.ResetAt.ToString("MM-dd") : "--"));
                compactToolTipLines.Add("WEEK " + snapshot.Weekly.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + (snapshot.Weekly.HasReset ? " | RESET " + snapshot.Weekly.ResetAt.ToString("MM-dd HH:mm") : ""));
            }
            compactResetText = String.Join("\n", compactLines.ToArray());
            toolTip.SetToolTip(expandButton, (chinese ? "返回完整面板" : "Back to full panel") + (compactToolTipLines.Count > 0 ? "\n" + String.Join("\n", compactToolTipLines.ToArray()) : ""));
            compactAccent = GetUsageAccent();
            statusValue.Text = "Updated " + snapshot.GeneratedAt.ToString("HH:mm:ss") + " | every 5s" + (snapshot.Partial ? " | partial history" : "");
            Invalidate();
        }
        catch
        {
            statusValue.Text = "Waiting for Codex session data...";
        }
        finally { refreshing = false; }
    }

    private void BeginValueAnimation()
    {
        valueAnimationTimer.Stop();
        valueAnimationFromFiveHour = animatedFiveHourRemaining;
        valueAnimationFromWeekly = animatedWeeklyRemaining;
        valueAnimationStarted = DateTime.UtcNow;
        if (!motionEnabled)
        {
            animatedFiveHourRemaining = lastHasFiveHour ? lastFiveHourRemainingPercent : 0;
            animatedWeeklyRemaining = lastHasWeekly ? lastWeeklyRemainingPercent : 0;
            UpdateAnimatedQuotaVisuals();
            return;
        }
        valueAnimationTimer.Start();
    }

    private void AdvanceValueAnimation()
    {
        double raw = Math.Min(1.0, (DateTime.UtcNow - valueAnimationStarted).TotalMilliseconds / ValueTransitionDurationMs);
        double eased = 1.0 - Math.Pow(1.0 - raw, 3.0);
        double fiveTarget = lastHasFiveHour ? lastFiveHourRemainingPercent : 0;
        double weeklyTarget = lastHasWeekly ? lastWeeklyRemainingPercent : 0;
        animatedFiveHourRemaining = valueAnimationFromFiveHour + (fiveTarget - valueAnimationFromFiveHour) * eased;
        animatedWeeklyRemaining = valueAnimationFromWeekly + (weeklyTarget - valueAnimationFromWeekly) * eased;
        UpdateAnimatedQuotaVisuals();
        if (raw >= 1.0) valueAnimationTimer.Stop();
    }

    private void UpdateAnimatedQuotaVisuals()
    {
        fiveHourProgressFill.Width = Math.Max(0, Math.Min(fiveHourProgressTrack.Width, (int)Math.Round(fiveHourProgressTrack.Width * animatedFiveHourRemaining / 100.0)));
        progressFill.Width = Math.Max(0, Math.Min(progressTrack.Width, (int)Math.Round(progressTrack.Width * animatedWeeklyRemaining / 100.0)));
        UpdatePillRegion(fiveHourProgressFill);
        UpdatePillRegion(progressFill);
        if (compactMode) Invalidate();
    }

    private void ApplyQuotaWindow(QuotaWindowSnapshot quota, Label value, Label detail, Label reset, Panel track, Panel fill, bool includePlan, string plan)
    {
        if (!quota.Available)
        {
            value.Text = "--%";
            detail.Text = chinese ? "暂无额度数据" : "No quota sample yet";
            reset.Text = "";
            value.ForeColor = Theme.Muted;
            fill.BackColor = Theme.Muted;
            return;
        }

        value.Text = quota.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        detail.Text = quota.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + (chinese ? "% 已用" : "% used") + (includePlan ? "  |  " + plan : "");
        reset.Text = quota.HasReset ? "RESET " + quota.ResetAt.ToString("MM-dd HH:mm") : "";
        Color accent = GetQuotaAccent(true, quota.RemainingPercent);
        fill.BackColor = accent;
        value.ForeColor = accent;
        toolTip.SetToolTip(value, (chinese ? "剩余 " : "Remaining ") + quota.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" + (quota.HasReset ? " | " + reset.Text : ""));
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
            progressFill.Width = Math.Max(0, Math.Min(progressTrack.Width, (int)Math.Round(progressTrack.Width * animatedWeeklyRemaining / 100.0)));
        if (fiveHourProgressTrack != null && fiveHourProgressFill != null)
            fiveHourProgressFill.Width = Math.Max(0, Math.Min(fiveHourProgressTrack.Width, (int)Math.Round(fiveHourProgressTrack.Width * animatedFiveHourRemaining / 100.0)));
        UpdatePillRegion(progressFill);
        UpdatePillRegion(fiveHourProgressFill);
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
            Color hoverTrack = Blend(Theme.Track, Theme.Border, 0.45F * compactHoverAmount);
            int ringWidth = Math.Max(3, diameter / 22);
            var outerBounds = new Rectangle((ClientSize.Width - diameter) / 2, (ClientSize.Height - diameter) / 2, diameter, diameter);
            int innerInset = ringWidth * 2 + Math.Max(2, diameter / 45);
            var innerBounds = Rectangle.Inflate(outerBounds, -innerInset, -innerInset);

            if (lastHasWeekly)
                DrawQuotaRing(e.Graphics, outerBounds, ringWidth, animatedWeeklyRemaining, GetQuotaAccent(true, lastWeeklyRemainingPercent), hoverTrack);
            if (lastHasFiveHour)
            {
                Rectangle targetBounds = lastHasWeekly ? innerBounds : outerBounds;
                DrawQuotaRing(e.Graphics, targetBounds, ringWidth, animatedFiveHourRemaining, GetQuotaAccent(true, lastFiveHourRemainingPercent), hoverTrack);
            }
            if (!lastHasFiveHour && !lastHasWeekly)
                DrawQuotaRing(e.Graphics, outerBounds, ringWidth, 0, Theme.Muted, hoverTrack);

            Color accent = Blend(compactAccent, Theme.Text, 0.22F * compactHoverAmount);
            float normalFontSize = Math.Max(14F, Math.Min(26F, Math.Min(ClientSize.Width, ClientSize.Height) * 0.21F));
            float hoverFontSize = Math.Max(10F, Math.Min(16F, Math.Min(ClientSize.Width, ClientSize.Height) * 0.15F));
            float fontSize = normalFontSize + (hoverFontSize - normalFontSize) * compactHoverAmount;
            float textY = ClientSize.Height * (0.25F - 0.01F * compactHoverAmount);
            float textHeight = ClientSize.Height * (0.62F - 0.35F * compactHoverAmount);
            RectangleF textBounds = new RectangleF(0, textY, ClientSize.Width, textHeight);
            using (var font = new Font(UiFontName, fontSize, FontStyle.Bold))
            using (var brush = new SolidBrush(accent))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                e.Graphics.DrawString(compactText, font, brush, textBounds, format);
            if (compactHoverAmount > 0.01F && !String.IsNullOrEmpty(compactResetText))
            {
                float resetFontSize = Math.Max(4.6F, Math.Min(5.8F, ClientSize.Width * 0.052F));
                RectangleF resetBounds = new RectangleF(4, ClientSize.Height * 0.49F, ClientSize.Width - 8, ClientSize.Height * 0.34F);
                using (var resetFont = new Font(UiFontName, resetFontSize, FontStyle.Regular))
                using (var resetBrush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * compactHoverAmount), Blend(Theme.Muted, Theme.Text, 0.18F))))
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

    private void DrawQuotaRing(Graphics graphics, Rectangle bounds, int width, double remainingPercent, Color accent, Color trackColor)
    {
        Color renderedAccent = Blend(accent, Theme.Text, 0.22F * compactHoverAmount);
        using (var track = new Pen(trackColor, width))
            graphics.DrawEllipse(track, bounds);
        using (var progress = new Pen(renderedAccent, width))
        {
            progress.StartCap = LineCap.Round;
            progress.EndCap = LineCap.Round;
            float sweep = (float)Math.Max(0, Math.Min(359.9, 360.0 * remainingPercent / 100.0));
            if (sweep > 0) graphics.DrawArc(progress, bounds, -90, sweep);
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
