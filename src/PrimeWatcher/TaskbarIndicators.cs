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
    private const double ItemWidth = 66;
    private const double RowHeight = 30;
    private static readonly IReadOnlyDictionary<DeviceKind, Geometry> Glyphs = new Dictionary<DeviceKind, Geometry>
    {
        [DeviceKind.Headset] = Geometry.Parse("M2,10 V8 A6,6 0 0 1 14,8 V10 M2,9 H5 V15 H3 A1,1 0 0 1 2,14 Z M14,9 H11 V15 H13 A1,1 0 0 0 14,14 Z"),
        [DeviceKind.Mouse] = Geometry.Parse("M8,1 A5,5 0 0 1 13,6 V10 A5,5 0 0 1 3,10 V6 A5,5 0 0 1 8,1 Z M8,1 V6"),
        [DeviceKind.Keyboard] = Geometry.Parse("M1,4 H15 V12 H1 Z M4,7 H5 M7,7 H8 M10,7 H11 M4,9 H5 M7,9 H12"),
        [DeviceKind.Gamepad] = Geometry.Parse("M5,4 H11 C14,4 15,8 15,11 C15,13 13,13 12,12 L10,10 H6 L4,12 C3,13 1,13 1,11 C1,8 2,4 5,4 Z M5,6 V9 M3.5,7.5 H6.5 M10,7.5 H10.2 M12.5,7.5 H12.7")
    };

    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1.25) };
    private Indicator[] indicators = [];
    private Indicator[] shown = [];
    private TaskbarLayout? placed;
    private bool checking;
    private bool enabled;
    private bool closed;
    private bool highContrast;

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
        timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public void Render(IReadOnlyList<DeviceState> devices, string? pinned, bool show)
    {
        enabled = show;
        indicators = devices.Where(device => device.Connected
                && (device.Device.Kind == DeviceKind.Gamepad || device.Device.Wireless || device.Device.Readings.Count > 0))
            .Select(device =>
            {
                var appearance = TrayIcon.Appearance(device);
                bool selected = string.Equals(device.Device.Id, pinned, StringComparison.OrdinalIgnoreCase);
                bool hasReading = appearance.Percent.HasValue || appearance.Level.HasValue;
                string value = appearance.Percent is int percent ? $"{percent}%"
                    : appearance.Level is BatteryLevel level ? LevelSymbol(level) : "—";
                return new Indicator(device.Device.Kind, value,
                    $"{device.Device.Name} · {device.Charge} · {device.Detail}", hasReading, selected);
            })
            .GroupBy(item => item.Kind)
            .Select(group => group.OrderByDescending(item => item.Selected)
                .ThenByDescending(item => item.HasReading).First())
            .ToArray();

        if (!enabled || indicators.Length == 0)
        {
            timer.Stop();
            Hide();
            placed = null;
            return;
        }

        if (placed is not null)
            Place(placed);
        if (!timer.IsEnabled)
        {
            timer.Start();
            _ = RefreshAsync();
        }
    }

    private static string LevelSymbol(BatteryLevel level) => level switch
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
            TaskbarLayout? layout = await Task.Run(TaskbarLayout.Read);
            if (closed)
                return;
            if (enabled && indicators.Length > 0 && layout is not null)
                Place(layout);
            else
            {
                Hide();
                placed = null;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            if (!closed)
            {
                Hide();
                placed = null;
            }
        }
        finally
        {
            checking = false;
        }
    }

    private void Place(TaskbarLayout layout)
    {
        int perItem = (int)Math.Ceiling(ItemWidth * layout.Scale);
        int capacity = Math.Max(0, (layout.Right - layout.Left) / perItem);
        Indicator[] visible = indicators.OrderByDescending(item => item.Selected).ThenBy(item => item.Kind)
            .Take(capacity).OrderBy(item => item.Kind).ToArray();
        if (visible.Length == 0)
        {
            Hide();
            placed = null;
            return;
        }

        bool widthChanged = shown.Length != visible.Length;
        bool contrastChanged = highContrast != SystemParameters.HighContrast;
        highContrast = SystemParameters.HighContrast;
        if (placed?.Light != layout.Light || contrastChanged || !shown.SequenceEqual(visible))
        {
            var foreground = highContrast ? SystemColors.WindowTextBrush : layout.Light ? Brushes.Black : Brushes.White;
            row.Children.Clear();
            foreach (var indicator in visible)
                row.Children.Add(CreateItem(indicator, foreground));
            shown = visible;
        }

        if (!IsVisible || placed != layout || widthChanged)
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            int width = perItem * visible.Length;
            int height = (int)Math.Ceiling(RowHeight * layout.Scale);
            int x = layout.Right - width;
            int y = layout.Top + (layout.Height - height) / 2;
            if (!IsVisible)
            {
                SetWindowPos(handle, new IntPtr(-1), x, y, width, height, 0x0010);
                Show();
            }
            SetWindowPos(handle, new IntPtr(-1), x, y, width, height, 0x0010);
        }
        placed = layout;
    }

    private static FrameworkElement CreateItem(Indicator indicator, Brush foreground)
    {
        var item = new StackPanel
        {
            Width = ItemWidth,
            Height = RowHeight,
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            ToolTip = indicator.Description
        };
        AutomationProperties.SetName(item, indicator.Description);
        item.Children.Add(new Path
        {
            Data = Glyphs[indicator.Kind],
            Width = 16,
            Height = 16,
            Margin = new Thickness(4, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Stroke = foreground,
            StrokeThickness = 1.25,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        item.Children.Add(new TextBlock
        {
            Text = indicator.Value,
            Foreground = foreground,
            FontSize = 13,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return item;
    }

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, -20);
        SetWindowLong(handle, -20, style | 0x08000000 | 0x00000080);
    }

    protected override void OnClosed(EventArgs args)
    {
        closed = true;
        enabled = false;
        timer.Stop();
        base.OnClosed(args);
    }

    private sealed record Indicator(DeviceKind Kind, string Value, string Description, bool HasReading, bool Selected);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
