using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class LayoutProbe
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [STAThread]
    private static void Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(output);
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using (var form = new WidgetForm(false))
        {
            form.Show();
            Pump(700);
            ApplyStressText(form);
            Capture(form, Path.Combine(output, "panel.png"));
            List<string> report = InspectLabels(form);
            bool topLeftClipped = form.Region != null && !form.Region.IsVisible(0, 0);
            bool topRightClipped = form.Region != null && !form.Region.IsVisible(form.ClientSize.Width - 1, 0);
            bool bottomLeftClipped = form.Region != null && !form.Region.IsVisible(0, form.ClientSize.Height - 1);
            bool bottomRightClipped = form.Region != null && !form.Region.IsVisible(form.ClientSize.Width - 1, form.ClientSize.Height - 1);
            report.Insert(1, "Corners=" + (topLeftClipped && topRightClipped && bottomLeftClipped && bottomRightClipped ? "CLIPPED" : "FAILED"));
            File.WriteAllLines(Path.Combine(output, "panel-layout.txt"), report.ToArray());

            FieldInfo motion = typeof(WidgetForm).GetField("motionEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            if (motion != null) motion.SetValue(form, false);
            MethodInfo compact = typeof(WidgetForm).GetMethod("SetCompactMode", BindingFlags.Instance | BindingFlags.NonPublic);
            compact.Invoke(form, new object[] { true });
            Pump(250);
            Capture(form, Path.Combine(output, "compact.png"));
        }
    }

    private static void Pump(int milliseconds)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static void Capture(Form form, string path)
    {
        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            float dpi = GetDpiForWindow(form.Handle);
            bitmap.SetResolution(dpi, dpi);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void ApplyStressText(WidgetForm form)
    {
        SetLabel(form, "todayValue", "999.99M");
        SetLabel(form, "inputValue", "IN   999.99M");
        SetLabel(form, "outputValue", "OUT  999.99M");
        SetLabel(form, "cacheValue", "CACHE 999.99M");
        SetLabel(form, "fiveHourValue", "100%");
        SetLabel(form, "weeklyValue", "100%");
        SetLabel(form, "fiveHourDetail", "100% 已用");
        SetLabel(form, "weeklyDetail", "100% 已用  |  PLUS");
        SetLabel(form, "fiveHourResetValue", "RESET 09-17 17:59");
        SetLabel(form, "resetValue", "RESET 09-17 17:59");
    }

    private static void SetLabel(WidgetForm form, string fieldName, string text)
    {
        FieldInfo field = typeof(WidgetForm).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        var label = field == null ? null : field.GetValue(form) as Label;
        if (label != null) label.Text = text;
    }

    private static List<string> InspectLabels(Control root)
    {
        var lines = new List<string>();
        lines.Add("Form=" + root.ClientSize.Width + "x" + root.ClientSize.Height + " DeviceDPI=" + root.DeviceDpi + " WindowDPI=" + GetDpiForWindow(root.Handle));
        foreach (Control control in Descendants(root))
        {
            var label = control as Label;
            if (label == null || !label.Visible || String.IsNullOrEmpty(label.Text)) continue;
            Size measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            bool clipped = measured.Width > label.ClientSize.Width || measured.Height > label.ClientSize.Height;
            lines.Add((clipped ? "CLIPPED " : "OK      ") + label.Text.Replace("\r", " ").Replace("\n", " ") +
                " measured=" + measured.Width + "x" + measured.Height + " bounds=" + label.ClientSize.Width + "x" + label.ClientSize.Height);
        }
        return lines;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }
}
