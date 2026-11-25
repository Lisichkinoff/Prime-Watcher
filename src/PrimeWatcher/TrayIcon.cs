using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace PrimeWatcher;

internal static class TrayIcon
{
    public static (string Key, int? Percent, BatteryLevel? Level, bool Offline) Appearance(DeviceState? state)
    {
        if (state is null)
            return ("idle", null, null, false);
        if (!state.Connected)
            return ("offline", null, null, true);

        var readings = state.Device.Readings;
        BatteryReading? reading = readings.FirstOrDefault(value => value.Label is "" or "Основной" && value.Percent.HasValue)
            ?? readings.Where(value => value.Percent.HasValue).OrderBy(value => value.Percent).FirstOrDefault()
            ?? readings.FirstOrDefault(value => value.Level.HasValue);
        return (reading?.Text ?? "unknown", reading?.Percent, reading?.Level, false);
    }

    public static Icon Create(int? percent, BatteryLevel? level, bool offline)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Color fill = offline ? Color.FromArgb(65, 73, 86) : percent <= 15 || level is BatteryLevel.Empty or BatteryLevel.Low
            ? Color.FromArgb(161, 47, 47) : Color.FromArgb(22, 78, 69);
        using var background = new SolidBrush(fill);
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var foreground = new SolidBrush(Color.White);
        if (percent.HasValue)
        {
            using var font = new Font("Segoe UI", percent == 100 ? 14 : 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(percent.Value.ToString(), font, foreground, new RectangleF(0, -1, 32, 32), format);
        }
        else if (offline)
        {
            using var stroke = new Pen(Color.White, 2.6f);
            graphics.DrawLine(stroke, 11, 11, 21, 21);
            graphics.DrawLine(stroke, 21, 11, 11, 21);
        }
        else
        {
            using var outline = new Pen(Color.White, 1.8f);
            graphics.DrawRectangle(outline, 6, 10, 18, 12);
            graphics.FillRectangle(foreground, 25, 14, 2, 4);
            int width = level switch { BatteryLevel.Full => 14, BatteryLevel.Medium => 9, BatteryLevel.Low => 4, _ => 0 };
            if (width > 0)
                graphics.FillRectangle(foreground, 8, 12, width, 8);
            else if (level is null)
                graphics.FillRectangle(foreground, 12, 15, 6, 2);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
