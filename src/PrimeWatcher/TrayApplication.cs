using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;

namespace PrimeWatcher;

internal sealed class TrayApplication : IDisposable
{
    private const string RepositoryUrl = "https://github.com/Lisichkinoff/Prime-Watcher";
    private readonly Application application;
    private readonly TrayPanel panel = new();
    private readonly BatteryWidget widget = new();
    private readonly TaskbarIndicators taskbar;
    private readonly DeviceMonitor monitor = new();
    private readonly UpdateManager updates = new(new GithubSource(RepositoryUrl, null, false));
    private readonly DispatcherTimer updateTimer = new();
    private readonly Forms.NotifyIcon tray = new();
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem restart;
    private readonly Forms.ToolStripMenuItem toggleWidget;
    private AppSettings settings = AppSettings.Load();
    private Icon? icon;
    private string? iconKey;
    private IReadOnlyList<DeviceState> devices = [];
    private bool scanned;
    private bool checking;
    private bool disposed;

    public TrayApplication(Application application)
    {
        this.application = application;
        taskbar = new TaskbarIndicators(() => OpenPanel());
        menu.Items.Add("Открыть Prime Watcher", null, (_, _) => OpenPanel());
        menu.Items.Add("Настройки", null, (_, _) => OpenPanel(PanelView.Settings));
        toggleWidget = new Forms.ToolStripMenuItem("Виджет поверх окон", null,
            (_, _) => ChangeSettings(settings with { ShowWidget = !settings.ShowWidget }));
        menu.Items.Add(toggleWidget);
        menu.Items.Add("Проверить обновления", null, (_, _) =>
        {
            OpenPanel(PanelView.Settings);
            panel.ScrollToUpdates();
            CheckUpdates();
        });
        restart = new Forms.ToolStripMenuItem("Перезапустить сейчас", null, (_, _) => ApplyUpdate()) { Visible = false };
        menu.Items.Add(restart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => application.Shutdown());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, args) =>
        {
            if (args.Button != Forms.MouseButtons.Left)
                return;
            if (panel.IsVisible)
                panel.Hide();
            else
                OpenPanel();
        };

        panel.PinRequested += id => ChangeSettings(settings with
        {
            PinnedDevice = string.Equals(id, settings.PinnedDevice, StringComparison.OrdinalIgnoreCase) ? null : id
        });
        panel.SettingsChanged += change => ChangeSettings(change(settings));
        widget.PositionChanged += position => ChangeSettings(settings with { WidgetLocation = position });
        panel.GuideDismissed += () => ChangeSettings(settings with { TrayGuideSeen = true });
        panel.UpdateCheckRequested += CheckUpdates;
        panel.RestartRequested += ApplyUpdate;
        monitor.Changed += snapshot => application.Dispatcher.BeginInvoke(() =>
        {
            devices = snapshot;
            scanned = true;
            Render();
        });
        updateTimer.Tick += (_, _) => CheckUpdates();
    }

    public void Start()
    {
        panel.SetOptions(settings);
        string version = updates.CurrentVersion?.ToString()
            ?? typeof(TrayApplication).Assembly.GetName().Version?.ToString(3) ?? "—";
        panel.SetUpdateVersion(version, updates.IsInstalled);
        if (updates.IsInstalled)
        {
            panel.ShowUpdate("Новые релизы скачиваются автоматически.");
            try { AppSettings.RegisterAutoStart(settings.AutoStart); }
            catch (Exception ex) { Trace.WriteLine(ex); }
        }
        else
        {
            panel.ShowUpdate("Автообновление доступно после установки Prime Watcher.");
        }
        Render();
        tray.Visible = true;
        HwndSource.FromHwnd(new WindowInteropHelper(panel).EnsureHandle()).AddHook(OnWindowMessage);
        monitor.Start();
        SystemEvents.PowerModeChanged += PowerModeChanged;

        application.Dispatcher.BeginInvoke(() =>
        {
            if (!settings.TrayGuideSeen)
            {
                panel.ShowGuide();
                var screen = Forms.Screen.PrimaryScreen;
                if (screen is null)
                    OpenPanel();
                else
                {
                    var area = screen.WorkingArea;
                    int y = area.Top > screen.Bounds.Top ? area.Top : area.Bottom;
                    panel.Open(new Point(area.Right - 10, y - 1));
                }
            }
            CheckUpdates();
        });
    }

    private void OpenPanel(PanelView view = PanelView.Devices) => panel.Open(Forms.Cursor.Position, view);

    private void ChangeSettings(AppSettings next)
    {
        var previous = settings;
        try
        {
            if (updates.IsInstalled && next.AutoStart != previous.AutoStart)
                AppSettings.RegisterAutoStart(next.AutoStart);
            next.Save();
            settings = next;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            if (updates.IsInstalled && next.AutoStart != previous.AutoStart)
            {
                try { AppSettings.RegisterAutoStart(previous.AutoStart); }
                catch (Exception rollback) { Trace.WriteLine(rollback); }
            }
            panel.ShowNotice("Не удалось сохранить настройки.");
        }
        panel.SetOptions(settings);
        Render();
    }

    private void Render()
    {
        panel.Render(devices, settings, scanned);
        taskbar.Render(devices, settings);
        widget.Render(devices, settings);
        toggleWidget.Checked = settings.ShowWidget;
        DeviceState? focus = settings.PinnedDevice is string id
            ? devices.FirstOrDefault(device => string.Equals(device.Device.Id, id, StringComparison.OrdinalIgnoreCase))
            : devices.FirstOrDefault(device => device.HasCurrentReading)
                ?? devices.FirstOrDefault(device => device.KeepLastChargeVisible)
                ?? devices.FirstOrDefault(device => device.Connected);
        var appearance = TrayIcon.Appearance(focus);
        if (focus is null && settings.PinnedDevice is not null && scanned)
            appearance = ("offline", null, null, true);
        bool showAppIcon = settings.PinnedDevice is null
            || appearance is { Percent: null, Level: null, Offline: false };
        string nextIconKey = showAppIcon ? "app" : appearance.Key;
        if (nextIconKey != iconKey)
        {
            var next = showAppIcon ? LoadAppIcon()
                : TrayIcon.Create(appearance.Percent, appearance.Level, appearance.Offline, focus?.IsLastReading == true);
            tray.Icon = next;
            icon?.Dispose();
            icon = next;
            iconKey = nextIconKey;
        }

        string tooltip = focus is not null
            ? $"{focus.Device.Name} · {(focus.Connected || focus.KeepLastChargeVisible ? focus.Charge : "Отключено")}"
            : appearance.Offline ? "Prime Watcher · Устройство отключено" : "Prime Watcher";
        tray.Text = tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…";
    }

    private static Icon LoadAppIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("PrimeWatcher.ico", UriKind.Relative)).Stream;
        var size = Forms.SystemInformation.SmallIconSize;
        return new Icon(stream, size.Width, size.Height);
    }

    private bool ReadyToRestart => updates.IsInstalled && updates.UpdatePendingRestart is not null;

    private async void CheckUpdates()
    {
        if (checking || !updates.IsInstalled)
            return;

        updateTimer.Stop();
        checking = true;
        bool downloading = false;
        TimeSpan interval = TimeSpan.FromDays(1);
        try
        {
            if (updates.UpdatePendingRestart is { } pending)
            {
                ShowReadyUpdate(pending.Version.ToString());
                return;
            }
            restart.Visible = false;
            panel.ShowUpdate("Проверка обновлений…", busy: true);
            UpdateInfo? available = await updates.CheckForUpdatesAsync();
            if (disposed)
                return;
            if (available is null)
            {
                panel.ShowUpdate("Установлена актуальная версия. Проверка повторится автоматически.");
                return;
            }

            string version = available.TargetFullRelease.Version.ToString();
            downloading = true;
            panel.ShowUpdate($"Скачивание версии {version}…", busy: true, progress: 0);
            IProgress<int> progress = new Progress<int>(percent =>
            {
                if (downloading && !disposed)
                    panel.ShowUpdate($"Скачивание версии {version} · {percent}%", busy: true, progress: percent);
            });
            await updates.DownloadUpdatesAsync(available, progress.Report);
            downloading = false;
            if (disposed)
                return;
            ShowReadyUpdate(version);
            tray.ShowBalloonTip(3500, "Prime Watcher", $"Версия {version} установится при следующем запуске.", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            interval = TimeSpan.FromHours(1);
            if (!disposed)
                panel.ShowUpdate("Проверить или скачать обновление не удалось. Повторим через час.");
        }
        finally
        {
            downloading = false;
            checking = false;
            if (!disposed)
            {
                updateTimer.Interval = interval;
                updateTimer.Start();
            }
        }
    }

    private void ShowReadyUpdate(string version)
    {
        restart.Visible = true;
        panel.ShowUpdate($"Версия {version} скачана. Обновление установится при следующем запуске.", ready: true);
    }

    private void ApplyUpdate()
    {
        try
        {
            if (ReadyToRestart)
                updates.ApplyUpdatesAndRestart(updates.UpdatePendingRestart!);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            OpenPanel(PanelView.Settings);
            panel.ScrollToUpdates();
            panel.ShowUpdate("Перезапуск не удался. Обновление установится при следующем запуске.", ready: true);
        }
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume)
            monitor.RequestRefresh();
    }

    private IntPtr OnWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0219) // WM_DEVICECHANGE
            monitor.RequestRefresh();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        disposed = true;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        updateTimer.Stop();
        monitor.Dispose();
        tray.Dispose();
        menu.Dispose();
        icon?.Dispose();
    }
}
