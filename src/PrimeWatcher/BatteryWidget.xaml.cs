using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace PrimeWatcher;

internal partial class BatteryWidget : Window
{
    private const int ExtendedStyle = -20;
    private const int TransparentStyle = 0x00000020;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private static readonly IntPtr TopmostWindow = new(-1);
    private static readonly Color NormalFrame = Color.FromRgb(0x71, 0x63, 0x83);
    private static readonly Color AlertFrame = Color.FromRgb(0xD3, 0x72, 0x73);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer alertTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private IReadOnlyList<DeviceState> latestDevices = [];
    private AppSettings? latestSettings;
    private string? alertDeviceId;
    private int alertThreshold;
    private bool lowChargeReported;
    private WidgetPosition? location;
    private Point? dragOffset;
    private bool clickThrough;
    private bool moved;
    private IntPtr foreground;

    public event Action<WidgetPosition>? PositionChanged;

    public BatteryWidget()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (IsVisible && dragOffset is null)
                Place();
        };
        timer.Tick += (_, _) =>
        {
            if (dragOffset is not null)
                return;
            Place();
            IntPtr current = GetForegroundWindow();
            if (current != foreground)
            {
                foreground = current;
                SetWindowPos(new WindowInteropHelper(this).Handle, TopmostWindow, 0, 0, 0, 0, NoActivate | NoMove | NoSize);
            }
        };
        alertTimer.Tick += (_, _) =>
        {
            StopAlert();
            if (latestSettings is { } settings)
                Render(latestDevices, settings);
        };
    }

    public void Render(IReadOnlyList<DeviceState> devices, AppSettings settings)
    {
        latestDevices = devices;
        latestSettings = settings;
        if (!settings.ShowWidget)
        {
            lowChargeReported = false;
            StopAlert();
            HideWidget();
            return;
        }

        DeviceState? pinned = settings.PinnedDevice is string id
            ? devices.FirstOrDefault(device => string.Equals(device.Device.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;
        UpdateAlert(pinned, settings);
        if (settings.WidgetOnlyWhenLow && !alertTimer.IsEnabled)
        {
            HideWidget();
            return;
        }

        var selected = settings.WidgetUseTaskbarSelection ? settings.GetTaskbarDevices(devices)
            : devices.Where(device => settings.IsWidgetDeviceEnabled(device.Device.Id));
        if (alertTimer.IsEnabled && pinned is not null)
        {
            selected = settings.WidgetOnlyWhenLow ? [pinned]
                : selected.Where(device => !string.Equals(device.Device.Id, pinned.Device.Id, StringComparison.OrdinalIgnoreCase))
                    .Prepend(pinned);
        }

        var rows = selected.Select(device =>
        {
            var reading = TrayIcon.Appearance(device);
            string value = reading.Offline ? "Откл." : reading.Percent is int percent ? $"{percent}%"
                : reading.Level is BatteryLevel level ? TaskbarIndicators.LevelSymbol(level) : "—";
            bool low = device.HasCurrentReading && IsLow(reading.Percent, reading.Level, settings.WidgetLowCharge);
            return new Row(device.Device.Kind, device.Device.Name, value, low, reading.Offline || device.IsLastReading,
                $"{device.Device.Name} · {device.Charge} · {device.Detail}");
        }).ToArray();
        if (Devices.ItemsSource is not Row[] shown || !shown.SequenceEqual(rows))
            Devices.ItemsSource = rows;
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        clickThrough = settings.WidgetClickThrough;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        SetInputStyle(handle);
        if (dragOffset is null)
            location = settings.WidgetLocation;
        if (!IsVisible)
        {
            Place();
            Show();
            UpdateLayout();
            Place();
            foreground = IntPtr.Zero;
            timer.Start();
        }
        else if (dragOffset is null)
        {
            Place();
        }
    }

    private static bool IsLow(int? percent, BatteryLevel? level, int threshold) =>
        percent <= threshold || level is BatteryLevel.Low or BatteryLevel.Empty;

    private void UpdateAlert(DeviceState? pinned, AppSettings settings)
    {
        if (!string.Equals(alertDeviceId, settings.PinnedDevice, StringComparison.OrdinalIgnoreCase)
            || alertThreshold != settings.WidgetLowCharge)
        {
            alertDeviceId = settings.PinnedDevice;
            alertThreshold = settings.WidgetLowCharge;
            lowChargeReported = false;
            StopAlert();
        }

        if (pinned is not { HasCurrentReading: true })
            return;

        var reading = TrayIcon.Appearance(pinned);
        if (!IsLow(reading.Percent, reading.Level, alertThreshold))
        {
            lowChargeReported = false;
            StopAlert();
            return;
        }

        if (lowChargeReported)
            return;
        lowChargeReported = true;
        LowChargeNotice.Visibility = Visibility.Visible;
        FrameColor.Color = AlertFrame;
        if (SystemParameters.ClientAreaAnimation)
        {
            FrameColor.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                From = AlertFrame,
                To = Colors.MistyRose,
                Duration = TimeSpan.FromMilliseconds(550),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3),
                FillBehavior = FillBehavior.Stop
            });
        }
        alertTimer.Start();
    }

    private void StopAlert()
    {
        if (!alertTimer.IsEnabled)
            return;
        alertTimer.Stop();
        LowChargeNotice.Visibility = Visibility.Collapsed;
        FrameColor.BeginAnimation(SolidColorBrush.ColorProperty, null);
        FrameColor.Color = NormalFrame;
    }

    private void HideWidget()
    {
        timer.Stop();
        if (IsVisible)
            Hide();
    }

    private void Place()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        var screen = location is { } saved ? Forms.Screen.FromPoint(new Point(saved.X, saved.Y)) : Forms.Screen.PrimaryScreen;
        if (screen is null)
            return;
        Rectangle area = screen.WorkingArea;

        if (!IsVisible)
            MoveWindow(handle, area.Left, area.Top);
        double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        int width = (int)Math.Ceiling(Width * scale);
        int height = (int)Math.Ceiling(Math.Max(ActualHeight, 40) * scale);
        int x = Math.Clamp(location?.X ?? area.Right - width - 22, area.Left, Math.Max(area.Left, area.Right - width));
        int y = Math.Clamp(location?.Y ?? area.Top + 24, area.Top, Math.Max(area.Top, area.Bottom - height));
        MoveWindow(handle, x, y);
    }

    private static void MoveWindow(IntPtr handle, int x, int y)
    {
        if (!GetWindowRect(handle, out NativeRect current) || current.Left != x || current.Top != y)
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, NoActivate | NoZOrder | NoSize);
    }

    private void BeginDrag(object sender, MouseButtonEventArgs args)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (clickThrough || !GetWindowRect(handle, out NativeRect window) || !Mouse.Capture((IInputElement)sender))
            return;

        Point cursor = Forms.Cursor.Position;
        dragOffset = new Point(cursor.X - window.Left, cursor.Y - window.Top);
        moved = false;
        args.Handled = true;
    }

    private void ContinueDrag(object sender, MouseEventArgs args)
    {
        if (dragOffset is not Point offset)
            return;

        Point cursor = Forms.Cursor.Position;
        var handle = new WindowInteropHelper(this).Handle;
        int x = cursor.X - offset.X;
        int y = cursor.Y - offset.Y;
        if (GetWindowRect(handle, out NativeRect window) && window.Left == x && window.Top == y)
            return;
        MoveWindow(handle, x, y);
        moved = true;
    }

    private void EndDrag(object sender, MouseButtonEventArgs args)
    {
        if (dragOffset is null)
            return;
        FinishDrag();
        Mouse.Capture(null);
        args.Handled = true;
    }

    private void DragCaptureLost(object sender, MouseEventArgs args)
    {
        if (dragOffset is not null)
            FinishDrag();
    }

    private void FinishDrag()
    {
        dragOffset = null;
        if (!moved || !GetWindowRect(new WindowInteropHelper(this).Handle, out NativeRect window))
            return;
        location = new WidgetPosition(window.Left, window.Top);
        Place();
        if (GetWindowRect(new WindowInteropHelper(this).Handle, out window))
            PositionChanged?.Invoke(new WidgetPosition(window.Left, window.Top));
    }

    private void SetInputStyle(IntPtr handle)
    {
        int current = GetWindowLong(handle, ExtendedStyle);
        int style = current | NoActivateStyle | ToolWindowStyle;
        style = clickThrough ? style | TransparentStyle : style & ~TransparentStyle;
        if (current != style)
            SetWindowLong(handle, ExtendedStyle, style);
    }

    protected override void OnClosed(EventArgs args)
    {
        timer.Stop();
        alertTimer.Stop();
        base.OnClosed(args);
    }

    private sealed record Row(DeviceKind Kind, string Name, string Charge, bool Low, bool Muted, string Description)
    {
        public Geometry Glyph => TaskbarIndicators.DeviceGlyph(Kind);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
