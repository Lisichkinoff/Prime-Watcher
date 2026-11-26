using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PrimeWatcher;

internal sealed class TaskbarIndicators : Window
{
    private const double ValueItemWidth = 66;
    private const double IconItemWidth = 30;
    private const double RowHeight = 30;
    private const int NoSize = 0x0001;
    private const int NoMove = 0x0002;
    private const int NoActivate = 0x0010;
    private const uint ForegroundEvent = 0x0003;
    private const uint ReorderEvent = 0x8004;
    private const uint SkipOwnProcess = 0x0002;
    private static readonly IntPtr TopmostWindow = new(-1);
    private static readonly IReadOnlyDictionary<DeviceKind, Geometry> Glyphs = new Dictionary<DeviceKind, Geometry>
    {
        [DeviceKind.Headset] = Geometry.Parse("M2,10 V8 A6,6 0 0 1 14,8 V10 M2,9 H5 V15 H3 A1,1 0 0 1 2,14 Z M14,9 H11 V15 H13 A1,1 0 0 0 14,14 Z"),
        [DeviceKind.Mouse] = Geometry.Parse("M8,1 A5,5 0 0 1 13,6 V10 A5,5 0 0 1 3,10 V6 A5,5 0 0 1 8,1 Z M8,1 V6"),
        [DeviceKind.Keyboard] = Geometry.Parse("M1,4 H15 V12 H1 Z M4,7 H5 M7,7 H8 M10,7 H11 M4,9 H5 M7,9 H12"),
        [DeviceKind.Gamepad] = Geometry.Parse("M5,4 H11 C14,4 15,8 15,11 C15,13 13,13 12,12 L10,10 H6 L4,12 C3,13 1,13 1,11 C1,8 2,4 5,4 Z M5,6 V9 M3.5,7.5 H6.5 M10,7.5 H10.2 M12.5,7.5 H12.7"),
        [DeviceKind.Other] = Geometry.Parse("M2,5 H13 V11 H2 Z M13,7 H15 V9 H13 M5,8 H10")
    };

    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer layoutTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer orderTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly WinEventCallback orderChanged;
    private IntPtr handle;
    private IntPtr taskbar;
    private IntPtr reorderHook;
    private IntPtr foregroundHook;
    private Indicator[] indicators = [];
    private Indicator[] shown = [];
    private TaskbarLayout? placed;
    private bool checking;
    private bool enabled;
    private bool showOverFullscreen;
    private bool closed;
    private bool highContrast;
    private double itemWidth = ValueItemWidth;
    private int unavailableReads;

    public TaskbarIndicators(Action openPanel)
    {
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "Prime Watcher · Заряд на панели задач";
        Content = row;
        row.MouseLeftButtonUp += (_, _) => openPanel();
        layoutTimer.Tick += (_, _) => _ = RefreshAsync();
        orderChanged = OnWindowOrderChanged;
        orderTimer.Tick += (_, _) => KeepAboveTaskbar();
    }

    public void Render(IReadOnlyList<DeviceState> devices, AppSettings settings)
    {
        enabled = settings.ShowTaskbarIndicators;
        showOverFullscreen = settings.TaskbarShowOverFullscreen;
        itemWidth = settings.TaskbarShowValues ? ValueItemWidth : IconItemWidth;
        indicators = settings.GetTaskbarDevices(devices)
            .Select(device =>
            {
                var appearance = TrayIcon.Appearance(device);
                bool selected = string.Equals(device.Device.Id, settings.PinnedDevice, StringComparison.OrdinalIgnoreCase);
                string? value = !settings.TaskbarShowValues ? null : appearance.Percent is int percent ? $"{percent}%"
                    : appearance.Level is BatteryLevel level ? LevelSymbol(level) : "—";
                return new Indicator(device.Device.Kind, value,
                    $"{device.Device.Name} · {device.Charge} · {device.Detail}", selected, device.IsLastReading);
            }).ToArray();

        if (!enabled || indicators.Length == 0)
        {
            layoutTimer.Stop();
            ClearPlacement();
            return;
        }

        if (placed is not null)
            Place(placed);
        if (!layoutTimer.IsEnabled)
        {
            layoutTimer.Start();
            _ = RefreshAsync();
        }
    }

    internal static string LevelSymbol(BatteryLevel level) => level switch
    {
        BatteryLevel.Empty => "▱",
        BatteryLevel.Low => "▰▱▱",
        BatteryLevel.Medium => "▰▰▱",
        _ => "▰▰▰"
    };

    private async Task RefreshAsync()
    {
        if (checking || !enabled || indicators.Length == 0)
            return;
        checking = true;
        try
        {
            var result = await Task.Run(TaskbarLayout.Read);
            ApplyLayout(result.Layout, result.Unavailable);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            ApplyLayout(null, unavailable: true);
        }
        finally
        {
            checking = false;
        }
    }

    private void ApplyLayout(TaskbarLayout? layout, bool unavailable)
    {
        if (closed || !enabled || indicators.Length == 0)
            return;
        if (layout is not null)
        {
            unavailableReads = 0;
            Place(layout);
        }
        else if (!unavailable || ++unavailableReads >= 3)
        {
            ClearPlacement();
        }
    }

    private void ClearPlacement()
    {
        StopTrackingOrder();
        Hide();
        placed = null;
        unavailableReads = 0;
    }

    private void Place(TaskbarLayout layout)
    {
        if (HideForFullscreen(layout.Taskbar))
            return;

        int perItem = (int)Math.Ceiling(itemWidth * layout.Scale);
        int capacity = Math.Max(0, (layout.Right - layout.Left) / perItem);
        Indicator[] visible = indicators.OrderByDescending(item => item.Selected).ThenBy(item => item.Kind)
            .ThenBy(item => item.LastReading)
            .Take(capacity).OrderBy(item => item.Kind).ToArray();
        if (visible.Length == 0)
        {
            ClearPlacement();
            return;
        }

        bool widthChanged = shown.Length != visible.Length || (shown[0].Value is null) != (visible[0].Value is null);
        bool contrastChanged = highContrast != SystemParameters.HighContrast;
        highContrast = SystemParameters.HighContrast;
        if (placed?.Light != layout.Light || contrastChanged || !shown.SequenceEqual(visible))
        {
            var foreground = highContrast ? SystemColors.WindowTextBrush : layout.Light ? Brushes.Black : Brushes.White;
            row.Children.Clear();
            foreach (var indicator in visible)
                row.Children.Add(CreateItem(indicator, foreground, itemWidth));
            shown = visible;
        }

        if (!IsVisible || placed != layout || widthChanged)
        {
            new WindowInteropHelper(this).EnsureHandle();
            int width = perItem * visible.Length;
            int height = (int)Math.Ceiling(RowHeight * layout.Scale);
            int x = layout.Right - width;
            int y = layout.Top + (layout.Height - height) / 2;
            if (!IsVisible)
            {
                SetWindowPos(handle, TopmostWindow, x, y, width, height, NoActivate);
                Show();
            }
            SetWindowPos(handle, TopmostWindow, x, y, width, height, NoActivate);
        }
        placed = layout;
        TrackWindowOrder(layout.Taskbar);
    }

    private void TrackWindowOrder(IntPtr shellTaskbar)
    {
        if (taskbar != shellTaskbar)
        {
            StopTrackingOrder();
            taskbar = shellTaskbar;
            GetWindowThreadProcessId(taskbar, out uint explorerProcess);
            if (explorerProcess != 0)
                reorderHook = SetWinEventHook(ReorderEvent, ReorderEvent, IntPtr.Zero, orderChanged,
                    explorerProcess, 0, SkipOwnProcess);
            foregroundHook = SetWinEventHook(ForegroundEvent, ForegroundEvent, IntPtr.Zero, orderChanged,
                0, 0, SkipOwnProcess);
            orderTimer.Start();
        }
        KeepAboveTaskbar();
    }

    private void OnWindowOrderChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (eventType == ForegroundEvent && HideForFullscreen(taskbar))
            return;
        KeepAboveTaskbar();
    }

    private bool HideForFullscreen(IntPtr shellTaskbar)
    {
        if (showOverFullscreen || shellTaskbar == IntPtr.Zero || !TaskbarLayout.IsFullscreen(shellTaskbar))
            return false;
        ClearPlacement();
        return true;
    }

    private void KeepAboveTaskbar()
    {
        // Explorer raises its own topmost window on interaction. Layout queries run separately
        // so an accessibility query cannot delay restoring the indicator's window order.
        if (taskbar != IntPtr.Zero && IsVisible && TaskbarLayout.IsAbove(taskbar, handle))
            SetWindowPos(handle, TopmostWindow, 0, 0, 0, 0, NoActivate | NoSize | NoMove);
    }

    private void StopTrackingOrder()
    {
        orderTimer.Stop();
        if (reorderHook != IntPtr.Zero)
            UnhookWinEvent(reorderHook);
        if (foregroundHook != IntPtr.Zero)
            UnhookWinEvent(foregroundHook);
        reorderHook = foregroundHook = taskbar = IntPtr.Zero;
    }

    private static FrameworkElement CreateItem(Indicator indicator, Brush foreground, double width)
    {
        var item = new StackPanel
        {
            Width = width,
            Height = RowHeight,
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            ToolTip = indicator.Description
        };
        AutomationProperties.SetName(item, indicator.Description);
        item.Children.Add(new Path
        {
            Data = DeviceGlyph(indicator.Kind),
            Width = 16,
            Height = 16,
            Margin = indicator.Value is null ? new Thickness(7, 0, 7, 0) : new Thickness(4, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Stroke = foreground,
            StrokeThickness = 1.25,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        if (indicator.Value is not null)
        {
            item.Children.Add(new TextBlock
            {
                Text = indicator.Value,
                Foreground = foreground,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = indicator.LastReading ? 0.72 : 1
            });
        }
        return item;
    }

    internal static Geometry DeviceGlyph(DeviceKind kind) => Glyphs[kind];

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, -20);
        SetWindowLong(handle, -20, style | 0x08000000 | 0x00000080);
    }

    protected override void OnClosed(EventArgs args)
    {
        closed = true;
        enabled = false;
        layoutTimer.Stop();
        StopTrackingOrder();
        base.OnClosed(args);
    }

    private sealed record Indicator(DeviceKind Kind, string? Value, string Description, bool Selected, bool LastReading);

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
