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
    private readonly DeviceMonitor monitor = new();
    private readonly UpdateManager updates = new(new GithubSource(RepositoryUrl, null, false));
    private readonly DispatcherTimer updateTimer = new();
    private readonly Forms.NotifyIcon tray = new();
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem restart;
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
        menu.Items.Add("Открыть Prime Watcher", null, (_, _) => OpenPanel());
        menu.Items.Add("Настройки", null, (_, _) => OpenPanel(settings: true));
        menu.Items.Add("Проверить обновления", null, (_, _) => CheckUpdates(manual: true));
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
        panel.AutoStartRequested += enabled => ChangeSettings(settings with { AutoStart = enabled });
        panel.GuideDismissed += () => ChangeSettings(settings with { TrayGuideSeen = true });
        panel.RestartRequested += ApplyUpdate;
        monitor.Changed += snapshot => application.Dispatcher.BeginInvoke(() =>
        {
            devices = snapshot;
            scanned = true;
            Render();
        });
        updateTimer.Tick += (_, _) => CheckUpdates(manual: false);
    }

    public void Start()
    {
        panel.SetAutoStart(settings.AutoStart);
        if (updates.IsInstalled)
        {
            try { AppSettings.RegisterAutoStart(settings.AutoStart); }
            catch (Exception ex) { Trace.WriteLine(ex); }
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
            CheckUpdates(manual: false);
        });
    }

    private void OpenPanel(bool settings = false) => panel.Open(Forms.Cursor.Position, settings);

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
            panel.ShowUpdate("Не удалось сохранить настройки.", ReadyToRestart);
        }
        panel.SetAutoStart(settings.AutoStart);
        Render();
    }

    private void Render()
    {
        panel.Render(devices, settings.PinnedDevice, scanned);
        DeviceState? focus = settings.PinnedDevice is string id
            ? devices.FirstOrDefault(device => string.Equals(device.Device.Id, id, StringComparison.OrdinalIgnoreCase))
            : devices.FirstOrDefault(device => device.Connected && device.Device.Readings.Count > 0)
                ?? devices.FirstOrDefault(device => device.Connected);
        var appearance = TrayIcon.Appearance(focus);
        if (focus is null && settings.PinnedDevice is not null && scanned)
            appearance = ("offline", null, null, true);
        if (appearance.Key != iconKey)
        {
            var next = TrayIcon.Create(appearance.Percent, appearance.Level, appearance.Offline);
            tray.Icon = next;
            icon?.Dispose();
            icon = next;
            iconKey = appearance.Key;
        }

        string tooltip = focus is not null
            ? $"{focus.Device.Name} · {(focus.Connected ? focus.Device.Charge : "Отключено")}"
            : appearance.Offline ? "Prime Watcher · Устройство отключено" : "Prime Watcher";
        tray.Text = tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…";
    }

    private bool ReadyToRestart => updates.IsInstalled && updates.UpdatePendingRestart is not null;

    private async void CheckUpdates(bool manual)
    {
        if (manual)
            OpenPanel();
        if (checking)
        {
            if (manual)
                panel.ShowUpdate("Идёт проверка обновлений…", false);
            return;
        }
        if (!updates.IsInstalled)
        {
            if (manual)
                panel.ShowUpdate("Автообновление доступно после установки Prime Watcher.", false);
            return;
        }

        updateTimer.Stop();
        checking = true;
        TimeSpan interval = TimeSpan.FromDays(1);
        try
        {
            restart.Visible = ReadyToRestart;
            if (ReadyToRestart)
            {
                panel.ShowUpdate("Обновление готово к установке.", true);
                return;
            }
            if (manual)
                panel.ShowUpdate("Проверка обновлений…", false);
            UpdateInfo? available = await updates.CheckForUpdatesAsync();
            if (available is null)
            {
                if (manual)
                    panel.ShowUpdate($"Установлена актуальная версия {updates.CurrentVersion}.", false);
                return;
            }

            if (manual)
                panel.ShowUpdate("Скачивание обновления…", false);
            await updates.DownloadUpdatesAsync(available);
            if (disposed)
                return;
            restart.Visible = true;
            panel.ShowUpdate($"Версия {available.TargetFullRelease.Version} готова к установке.", true);
            tray.ShowBalloonTip(3500, "Prime Watcher", "Обновление установится при следующем запуске.", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            interval = TimeSpan.FromHours(1);
            if (manual && !disposed)
                panel.ShowUpdate("Проверить или скачать обновление не удалось. Повторим через час.", false);
        }
        finally
        {
            checking = false;
            if (!disposed)
            {
                updateTimer.Interval = interval;
                updateTimer.Start();
            }
        }
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
            OpenPanel();
            panel.ShowUpdate("Перезапуск не удался. Обновление доступно при следующем запуске.", true);
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
