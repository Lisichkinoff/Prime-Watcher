using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PrimeWatcher;

internal sealed record TaskbarLayout(IntPtr Taskbar, int Left, int Top, int Right, int Height, double Scale, bool Light)
{
    public static (TaskbarLayout? Layout, bool Unavailable) Read()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var bar))
            return (null, true);
        if (!IsWindowVisible(taskbar) || bar.Width <= bar.Height)
            return (null, false);

        var monitor = Forms.Screen.FromHandle(taskbar).Bounds;
        double scale = Math.Max(96, GetDpiForWindow(taskbar)) / 96.0;
        int visibleHeight = Math.Min(bar.Bottom, monitor.Bottom) - Math.Max(bar.Top, monitor.Top);
        if (visibleHeight < Math.Ceiling(30 * scale) || visibleHeight != bar.Height || IsFullscreen(monitor, taskbar))
            return (null, false);

        IntPtr notificationArea = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notificationArea == IntPtr.Zero || !GetWindowRect(notificationArea, out var tray)
            || tray.Left <= bar.Left || tray.Right > bar.Right || tray.Width <= 0)
            return (null, true);

        int? left = null;
        try
        {
            left = LeftOfGap(taskbar, bar, tray.Left);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            Trace.WriteLine(ex);
        }

        // Explorer may temporarily expose only its native task-list window through UI Automation.
        IntPtr rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        IntPtr taskList = rebar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (taskList != IntPtr.Zero && IsWindowVisible(taskList) && GetWindowRect(taskList, out var tasks)
            && tasks.Width > 0 && tasks.Left >= bar.Left && tasks.Right <= bar.Right
            && tasks.Top < bar.Bottom && tasks.Bottom > bar.Top
            && (left is null || tasks.Right < tray.Left))
            left = Math.Max(left ?? bar.Left, tasks.Right);
        if (left is null)
            return (null, true);

        int margin = (int)Math.Ceiling(6 * scale);
        bool light = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is 1;
        return (new TaskbarLayout(taskbar, left.Value + margin, bar.Top, tray.Left - margin, bar.Height, scale, light), false);
    }

    private static int? LeftOfGap(IntPtr taskbar, NativeRect bar, int trayLeft)
    {
        AutomationElement root = AutomationElement.FromHandle(taskbar);
        var controls = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        int left = bar.Left;
        bool foundButton = false;
        using (cache.Activate())
        {
            foreach (AutomationElement item in root.FindAll(TreeScope.Descendants, controls))
            {
                Rect bounds = item.Cached.BoundingRectangle;
                if (item.Cached.IsOffscreen || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0
                    || bounds.Top < bar.Top - 1 || bounds.Bottom > bar.Bottom + 1 || bounds.Right > trayLeft)
                    continue;

                foundButton = true;
                left = Math.Max(left, (int)Math.Ceiling(bounds.Right));
            }
        }
        return foundButton ? left : null;
    }

    private static bool IsFullscreen(System.Drawing.Rectangle monitor, IntPtr taskbar)
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == GetShellWindow())
            return false;
        var className = new StringBuilder(128);
        GetClassName(window, className, className.Capacity);
        if (className.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd")
            return false;
        return DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<NativeRect>()) == 0
            && rect.Left <= monitor.Left && rect.Top <= monitor.Top
            && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom
            && IsAbove(window, taskbar);
    }

    internal static bool IsAbove(IntPtr window, IntPtr reference)
    {
        const uint previousWindow = 3;
        IntPtr previous = GetWindow(reference, previousWindow);
        for (int remaining = 128; previous != IntPtr.Zero && remaining > 0; remaining--)
        {
            if (previous == window)
                return true;
            previous = GetWindow(previous, previousWindow);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect rect, int size);
}
