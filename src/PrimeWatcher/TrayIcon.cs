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
        if (!state.Connected && !state.KeepLastChargeVisible)
            return ("offline", null, null, true);

        var readings = state.DisplayReadings;
        BatteryReading? reading = readings.FirstOrDefault(value => value.Label is "" or "Основной" && value.Percent.HasValue)
            ?? readings.Where(value => value.Percent.HasValue).OrderBy(value => value.Percent).FirstOrDefault()
            ?? readings.FirstOrDefault(value => value.Level.HasValue);
        string key = reading?.Text ?? "unknown";
        return (state.IsLastReading ? $"last:{key}" : key, reading?.Percent, reading?.Level, false);
    }

    public static Icon Create(int? percent, BatteryLevel? level, bool offline, bool lastReading = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Color tile = Color.FromArgb(0x36, 0x18, 0x4A);
        Color battery = offline || lastReading ? Color.FromArgb(0xB6, 0xAF, 0xC7)
            : percent <= 15 || level is BatteryLevel.Empty or BatteryLevel.Low ? Color.FromArgb(0xFF, 0xAD, 0xBD)
            : Color.FromArgb(0xD1, 0xBF, 0xFF);
        using var background = new SolidBrush(tile);
        using var foreground = new SolidBrush(battery);
        graphics.FillRoundedRectangle(background, new RectangleF(1, 1, 30, 30), new SizeF(7, 7));
        graphics.FillRoundedRectangle(foreground, new RectangleF(5, 7, 21, 18), new SizeF(3, 3));
        graphics.FillRectangle(foreground, 26, 13, 3, 6);
        if (percent.HasValue)
        {
            using var text = new SolidBrush(tile);
            using var font = new Font("Segoe UI", percent == 100 ? 11 : 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(percent.Value.ToString(), font, text, new RectangleF(4, 6, 23, 18), format);
        }
        else if (offline)
        {
            using var stroke = new Pen(tile, 2.6f);
            graphics.DrawLine(stroke, 11, 11, 21, 21);
            graphics.DrawLine(stroke, 21, 11, 11, 21);
        }
        else
        {
            using var bars = new SolidBrush(tile);
            int count = level switch { BatteryLevel.Full => 3, BatteryLevel.Medium => 2, BatteryLevel.Low => 1, _ => 0 };
            for (int i = 0; i < count; i++)
                graphics.FillRectangle(bars, 8 + 5 * i, 11, 4, 10);
            if (count == 0)
                graphics.FillRectangle(bars, 12, 15, 7, 2);
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
