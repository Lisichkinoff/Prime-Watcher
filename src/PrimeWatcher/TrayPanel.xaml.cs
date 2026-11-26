using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace PrimeWatcher;

internal enum PanelView { Devices, Settings, Widget }

internal partial class TrayPanel : Window
{
    public event Action<string>? PinRequested;
    public event Action<Func<AppSettings, AppSettings>>? SettingsChanged;
    public event Action? GuideDismissed;
    public event Action? UpdateCheckRequested;
    public event Action? RestartRequested;
    private Point anchor;
    private DeviceOption[] deviceOptions = [];
    private bool settingOptions;
    private bool updatesEnabled;

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

    public void Render(IReadOnlyList<DeviceState> devices, AppSettings settings, bool scanned)
    {
        var rows = devices.Select(device => new Row(device.Device.Id, device.Device.Name, device.Charge,
            device.Detail, device.Device.Kind,
            string.Equals(device.Device.Id, settings.PinnedDevice, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (Devices.ItemsSource is not Row[] current || !current.SequenceEqual(rows))
            Devices.ItemsSource = rows;
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = scanned ? "Подключённые устройства не найдены." : "Поиск подключённых устройств…";

        var options = devices.Where(state => state.Device.CanAppearOnTaskbar
                || !settings.IsTaskbarDeviceEnabled(state.Device.Id) || settings.IsWidgetDeviceEnabled(state.Device.Id))
            .Select(state => (state.Device.Id, state.Device.Name, state.Device.Transport)).ToArray();
        if (!options.SequenceEqual(deviceOptions.Select(option => option.Device)))
        {
            deviceOptions = options.Select(option => new DeviceOption(option)).ToArray();
            DeviceOptions.ItemsSource = deviceOptions;
            WidgetDeviceOptions.ItemsSource = deviceOptions;
            DeviceOptionsCard.Visibility = deviceOptions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var option in deviceOptions)
        {
            option.Enabled = settings.IsTaskbarDeviceEnabled(option.Id);
            option.WidgetEnabled = settings.IsWidgetDeviceEnabled(option.Id);
        }
        WidgetDevicesCard.Visibility = !settings.WidgetOnlyWhenLow && !settings.WidgetUseTaskbarSelection && deviceOptions.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        string? pinned = devices.FirstOrDefault(device => string.Equals(device.Device.Id, settings.PinnedDevice,
            StringComparison.OrdinalIgnoreCase))?.Device.Name;
        WidgetAlertHint.Text = pinned is not null ? $"Предупреждение о заряде · {pinned}"
            : settings.PinnedDevice is not null ? "Ожидание закреплённого устройства."
            : "Для предупреждений закрепите устройство во вкладке «Устройства».";
    }

    public void SetOptions(AppSettings settings)
    {
        settingOptions = true;
        AutoStart.IsChecked = settings.AutoStart;
        ShowTaskbarIndicators.IsChecked = settings.ShowTaskbarIndicators;
        TaskbarShowOverFullscreen.IsChecked = settings.TaskbarShowOverFullscreen;
        TaskbarShowValues.IsChecked = settings.TaskbarShowValues;
        TaskbarOnlyWithCharge.IsChecked = settings.TaskbarOnlyWithCharge;
        TaskbarOnePerKind.IsChecked = settings.TaskbarOnePerKind;
        HeadsetOption.IsChecked = settings.IsTaskbarKindEnabled(DeviceKind.Headset);
        MouseOption.IsChecked = settings.IsTaskbarKindEnabled(DeviceKind.Mouse);
        KeyboardOption.IsChecked = settings.IsTaskbarKindEnabled(DeviceKind.Keyboard);
        GamepadOption.IsChecked = settings.IsTaskbarKindEnabled(DeviceKind.Gamepad);
        OtherDeviceOption.IsChecked = settings.IsTaskbarKindEnabled(DeviceKind.Other);
        WidgetEnabled.IsChecked = settings.ShowWidget;
        WidgetOnlyWhenLow.IsChecked = settings.WidgetOnlyWhenLow;
        WidgetClickThrough.IsChecked = settings.WidgetClickThrough;
        WidgetUseTaskbarSelection.IsChecked = settings.WidgetUseTaskbarSelection;
        WidgetLowCharge.Value = settings.WidgetLowCharge;
        settingOptions = false;
    }

    public void ShowGuide() => Guide.Visibility = Visibility.Visible;

    public void SetUpdateVersion(string version, bool enabled)
    {
        updatesEnabled = enabled;
        InstalledVersion.Text = $"Установлена версия {version}";
        CheckForUpdates.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowUpdate(string message, bool ready = false, bool busy = false, int? progress = null)
    {
        UpdateStatus.Text = message;
        CheckForUpdates.IsEnabled = !busy;
        CheckForUpdates.Visibility = !ready && updatesEnabled ? Visibility.Visible : Visibility.Collapsed;
        Restart.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Visibility = progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (progress is int percent)
            DownloadProgress.Value = percent;
    }

    public void ShowNotice(string message)
    {
        NoticeMessage.Text = message;
        Notice.Visibility = Visibility.Visible;
    }

    public void ScrollToUpdates() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdatesCard.BringIntoView()));

    public void Open(Point cursor, PanelView view = PanelView.Devices)
    {
        if (IsVisible)
        {
            if (view != PanelView.Devices)
                SelectView(view);
            Activate();
            return;
        }

        anchor = cursor;
        SelectView(view);
        var screen = Forms.Screen.FromPoint(cursor);
        var helper = new WindowInteropHelper(this);
        IntPtr handle = helper.EnsureHandle();
        Rectangle area = screen.WorkingArea;
        // Move the hidden HWND first so WPF measures the panel at this monitor's DPI.
        MoveWindow(handle, area.Left + 8, area.Top + 8);
        double scale = Math.Max(1, GetDpiForWindow(handle)) / 96.0;
        MaxHeight = Math.Min(600, Math.Max(1, area.Height / scale - 24));
        MaxWidth = Math.Max(1, area.Width / scale - 24);
        double width = Math.Min(Width, MaxWidth);
        var body = (FrameworkElement)Content;
        body.Measure(new Size(width, MaxHeight));
        PositionPanel(width, body.DesiredSize.Height);
        Show();
        UpdateLayout();
        PositionPanel();
        Activate();
    }

    private void PositionPanel() => PositionPanel(ActualWidth, ActualHeight);

    private void PositionPanel(double panelWidth, double panelHeight)
    {
        var screen = Forms.Screen.FromPoint(anchor);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Rectangle area = screen.WorkingArea;
        double scale = Math.Max(1, GetDpiForWindow(handle)) / 96.0;
        int width = (int)Math.Ceiling(panelWidth * scale);
        int height = (int)Math.Ceiling(panelHeight * scale);
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

    private void SelectView(PanelView view)
    {
        SettingsView.Visibility = view == PanelView.Settings ? Visibility.Visible : Visibility.Collapsed;
        DeviceView.Visibility = view == PanelView.Devices ? Visibility.Visible : Visibility.Collapsed;
        WidgetView.Visibility = view == PanelView.Widget ? Visibility.Visible : Visibility.Collapsed;
        SettingsTab.Tag = view == PanelView.Settings ? "Selected" : null;
        DevicesTab.Tag = view == PanelView.Devices ? "Selected" : null;
        WidgetTab.Tag = view == PanelView.Widget ? "Selected" : null;
    }

    private void ShowDevices(object sender, RoutedEventArgs args) => SelectView(PanelView.Devices);
    private void ShowSettings(object sender, RoutedEventArgs args) => SelectView(PanelView.Settings);
    private void ShowWidgetSettings(object sender, RoutedEventArgs args) => SelectView(PanelView.Widget);
    private void ClosePanel(object sender, RoutedEventArgs args) => Hide();
    private void PinDevice(object sender, RoutedEventArgs args) => PinRequested?.Invoke((string)((Button)sender).Tag);
    private void AutoStartChanged(object sender, RoutedEventArgs args) =>
        SettingsChanged?.Invoke(settings => settings with { AutoStart = AutoStart.IsChecked == true });
    private void TaskbarIndicatorsChanged(object sender, RoutedEventArgs args) =>
        SettingsChanged?.Invoke(settings => settings with { ShowTaskbarIndicators = ShowTaskbarIndicators.IsChecked == true });
    private void TaskbarOptionsChanged(object sender, RoutedEventArgs args) => SettingsChanged?.Invoke(settings => settings with
    {
        TaskbarShowOverFullscreen = TaskbarShowOverFullscreen.IsChecked == true,
        TaskbarShowValues = TaskbarShowValues.IsChecked == true,
        TaskbarOnlyWithCharge = TaskbarOnlyWithCharge.IsChecked == true,
        TaskbarOnePerKind = TaskbarOnePerKind.IsChecked == true
    });

    private void TaskbarKindChanged(object sender, RoutedEventArgs args)
    {
        var option = (CheckBox)sender;
        var kind = Enum.Parse<DeviceKind>((string)option.Tag);
        SettingsChanged?.Invoke(settings => settings with
        {
            TaskbarHiddenKinds = option.IsChecked == true
                ? settings.TaskbarHiddenKinds.Where(hidden => hidden != kind).ToArray()
                : [..settings.TaskbarHiddenKinds, kind]
        });
    }

    private void TaskbarDeviceChanged(object sender, RoutedEventArgs args)
    {
        var option = (CheckBox)sender;
        string id = (string)option.Tag;
        SettingsChanged?.Invoke(settings => settings with
        {
            TaskbarHiddenDevices = option.IsChecked == true
                ? settings.TaskbarHiddenDevices.Where(hidden => !string.Equals(hidden, id, StringComparison.OrdinalIgnoreCase)).ToArray()
                : [..settings.TaskbarHiddenDevices, id]
        });
        option.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
    }

    private void WidgetOptionsChanged(object sender, RoutedEventArgs args) => SettingsChanged?.Invoke(settings => settings with
    {
        ShowWidget = WidgetEnabled.IsChecked == true,
        WidgetOnlyWhenLow = WidgetOnlyWhenLow.IsChecked == true,
        WidgetClickThrough = WidgetClickThrough.IsChecked == true,
        WidgetUseTaskbarSelection = WidgetUseTaskbarSelection.IsChecked == true
    });

    private void WidgetLowChargeChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!settingOptions)
            SettingsChanged?.Invoke(settings => settings with { WidgetLowCharge = (int)args.NewValue });
    }

    private void WidgetDeviceChanged(object sender, RoutedEventArgs args)
    {
        var option = (CheckBox)sender;
        string id = (string)option.Tag;
        SettingsChanged?.Invoke(settings => settings with
        {
            WidgetDevices = option.IsChecked == true ? [..settings.WidgetDevices, id]
                : settings.WidgetDevices.Where(selected => !string.Equals(selected, id, StringComparison.OrdinalIgnoreCase)).ToArray()
        });
        option.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
    }

    private void RestartClicked(object sender, RoutedEventArgs args) => RestartRequested?.Invoke();
    private void CheckForUpdatesClicked(object sender, RoutedEventArgs args) => UpdateCheckRequested?.Invoke();

    private void DismissGuide(object sender, RoutedEventArgs args)
    {
        Guide.Visibility = Visibility.Collapsed;
        GuideDismissed?.Invoke();
    }

    private sealed record Row(string Id, string Name, string Charge, string Detail, DeviceKind Kind, bool IsPinned)
    {
        public Geometry Glyph => TaskbarIndicators.DeviceGlyph(Kind);
        public string PinLabel => IsPinned ? "Открепить" : "Закрепить";
        public string PinDescription => $"{PinLabel} {Name} в трее";
    }

    private sealed class DeviceOption((string Id, string Name, string Transport) device) : INotifyPropertyChanged
    {
        private bool enabled = true;
        private bool widgetEnabled;
        public (string Id, string Name, string Transport) Device { get; } = device;
        public string Id => Device.Id;
        public string Name => Device.Name;
        public string Transport => Device.Transport;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Enabled
        {
            get => enabled;
            set => SetEnabled(ref enabled, value, nameof(Enabled));
        }

        public bool WidgetEnabled
        {
            get => widgetEnabled;
            set => SetEnabled(ref widgetEnabled, value, nameof(WidgetEnabled));
        }

        private void SetEnabled(ref bool field, bool value, string property)
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr above, int x, int y, int width, int height, uint flags);
}
