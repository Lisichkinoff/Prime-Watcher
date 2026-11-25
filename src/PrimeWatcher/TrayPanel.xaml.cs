using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace PrimeWatcher;

internal partial class TrayPanel : Window
{
    public event Action<string>? PinRequested;
    public event Action<bool>? AutoStartRequested;
    public event Action? GuideDismissed;
    public event Action? RestartRequested;
    private Point anchor;

    public TrayPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
                PositionPanel();
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
                Hide();
        };
    }

    public void Render(IReadOnlyList<DeviceState> devices, string? pinned, bool scanned)
    {
        var rows = devices.Select(device => new Row(device.Device.Id, device.Device.Name, device.Charge,
            device.Detail, string.Equals(device.Device.Id, pinned, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (Devices.ItemsSource is not Row[] current || !current.SequenceEqual(rows))
            Devices.ItemsSource = rows;
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = scanned ? "Подключённые устройства не найдены." : "Поиск подключённых устройств…";
    }

    public void SetAutoStart(bool enabled) => AutoStart.IsChecked = enabled;

    public void ShowGuide() => Guide.Visibility = Visibility.Visible;

    public void ShowUpdate(string message, bool ready)
    {
        UpdateMessage.Text = message;
        UpdateBanner.Visibility = Visibility.Visible;
        Restart.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Open(Point cursor, bool settings = false)
    {
        anchor = cursor;
        if (settings)
            Settings.Visibility = Visibility.Visible;

        var screen = Forms.Screen.FromPoint(cursor);
        var helper = new WindowInteropHelper(this);
        IntPtr handle = helper.EnsureHandle();
        Rectangle area = screen.WorkingArea;
        // Move the hidden HWND first so WPF measures the panel at this monitor's DPI.
        if (!IsVisible)
            MoveWindow(handle, area.Left + 8, area.Top + 8);
        double scale = Math.Max(1, GetDpiForWindow(handle)) / 96.0;
        MaxHeight = Math.Min(610, Math.Max(220, area.Height / scale - 24));
        MaxWidth = Math.Max(220, area.Width / scale - 24);
        Show();
        UpdateLayout();
        PositionPanel();
        Activate();
    }

    private void PositionPanel()
    {
        var screen = Forms.Screen.FromPoint(anchor);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Rectangle area = screen.WorkingArea;
        double scale = Math.Max(1, GetDpiForWindow(handle)) / 96.0;
        int width = (int)Math.Ceiling(ActualWidth * scale);
        int height = (int)Math.Ceiling(ActualHeight * scale);
        int x = Math.Clamp(anchor.X - width + 20, area.Left + 8, Math.Max(area.Left + 8, area.Right - width - 8));
        int preferredY = area.Top > screen.Bounds.Top ? anchor.Y + 12 : anchor.Y - height - 12;
        int y = Math.Clamp(preferredY, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - height - 8));
        MoveWindow(handle, x, y);
    }

    private static void MoveWindow(IntPtr handle, int x, int y) =>
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);

    protected override void OnClosing(CancelEventArgs args)
    {
        args.Cancel = true;
        Hide();
        base.OnClosing(args);
    }

    private void ToggleSettings(object sender, RoutedEventArgs args) =>
        Settings.Visibility = Settings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ClosePanel(object sender, RoutedEventArgs args) => Hide();
    private void PinDevice(object sender, RoutedEventArgs args) => PinRequested?.Invoke((string)((Button)sender).Tag);
    private void AutoStartChanged(object sender, RoutedEventArgs args) => AutoStartRequested?.Invoke(AutoStart.IsChecked == true);
    private void RestartClicked(object sender, RoutedEventArgs args) => RestartRequested?.Invoke();

    private void DismissGuide(object sender, RoutedEventArgs args)
    {
        Guide.Visibility = Visibility.Collapsed;
        GuideDismissed?.Invoke();
    }

    private sealed record Row(string Id, string Name, string Charge, string Detail, bool IsPinned)
    {
        public string PinLabel => IsPinned ? "Открепить" : "Закрепить";
        public string PinDescription => $"{PinLabel} {Name} в трее";
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr above, int x, int y, int width, int height, uint flags);
}
