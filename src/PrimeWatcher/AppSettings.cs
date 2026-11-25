using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PrimeWatcher;

internal sealed record AppSettings(bool AutoStart = true, bool TrayGuideSeen = false, string? PinnedDevice = null)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PrimeWatcher";
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimeWatcherSettings");
    private static readonly string SettingsFile = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new();
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
