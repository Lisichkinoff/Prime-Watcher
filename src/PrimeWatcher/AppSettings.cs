using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace PrimeWatcher;

internal sealed record AppSettings(bool AutoStart = true, bool TrayGuideSeen = false, string? PinnedDevice = null)
{
    public bool ShowTaskbarIndicators { get; init; } = true;
    public bool TaskbarOnlyWithCharge { get; init; }
    public bool TaskbarOnePerKind { get; init; } = true;
    public bool TaskbarShowValues { get; init; } = true;
    public DeviceKind[] TaskbarHiddenKinds { get; init; } = [];
    public string[] TaskbarHiddenDevices { get; init; } = [];
    public bool ShowWidget { get; init; }
    public bool WidgetOnlyWhenLow { get; init; }
    public bool WidgetClickThrough { get; init; }
    public bool WidgetUseTaskbarSelection { get; init; } = true;
    public string[] WidgetDevices { get; init; } = [];
    public int WidgetLowCharge { get; init; } = 15;
    public WidgetPosition? WidgetLocation { get; init; }

    public bool IsTaskbarKindEnabled(DeviceKind kind) => !TaskbarHiddenKinds.Contains(kind);
    public bool IsTaskbarDeviceEnabled(string id) => !TaskbarHiddenDevices.Contains(id, StringComparer.OrdinalIgnoreCase);
    public bool IsWidgetDeviceEnabled(string id) => WidgetDevices.Contains(id, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<DeviceState> GetTaskbarDevices(IEnumerable<DeviceState> devices)
    {
        var candidates = devices.Where(device => (device.Connected && device.Device.CanAppearOnTaskbar
                || device.KeepLastChargeVisible)
            && IsTaskbarKindEnabled(device.Device.Kind) && IsTaskbarDeviceEnabled(device.Device.Id)
            && (!TaskbarOnlyWithCharge || device.DisplayReadings.Count > 0));
        return TaskbarOnePerKind ? candidates.GroupBy(device => device.Device.Kind).Select(group => group
                .OrderByDescending(device => string.Equals(device.Device.Id, PinnedDevice, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(device => device.HasCurrentReading)
                .ThenByDescending(device => device.DisplayReadings.Count > 0)
                .ThenByDescending(device => device.Connected).First())
            : candidates;
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PrimeWatcher";
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimeWatcherSettings");
    private static readonly string SettingsFile = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) is { } saved)
                return saved with
                {
                    TaskbarHiddenKinds = saved.TaskbarHiddenKinds ?? [],
                    TaskbarHiddenDevices = saved.TaskbarHiddenDevices ?? [],
                    WidgetDevices = saved.WidgetDevices ?? [],
                    WidgetLowCharge = Math.Clamp(saved.WidgetLowCharge, 5, 40)
                };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.WriteLine(ex);
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, SettingsFile, overwrite: true);
    }

    public static void RegisterAutoStart(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (!enabled)
        {
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
            return;
        }

        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable.");
        using RegistryKey target = Registry.CurrentUser.CreateSubKey(RunKey);
        target.SetValue(RunValue, $"\"{executable}\"");
    }
}

internal sealed record WidgetPosition(int X, int Y);
