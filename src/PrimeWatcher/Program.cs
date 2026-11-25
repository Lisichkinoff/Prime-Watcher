using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Velopack;

namespace PrimeWatcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => AppSettings.RegisterAutoStart(false))
            .Run();

        if (args is ["--diagnostics", var report])
            return Task.Run(() => WriteDiagnosticsAsync(report)).GetAwaiter().GetResult();

        using var instance = new Mutex(true, @"Local\PrimeWatcher.Tray", out bool first);
        if (!first)
            return 0;

        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var tray = new TrayApplication(application);
            tray.Start();
            return application.Run();
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static async Task<int> WriteDiagnosticsAsync(string path)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var devices = new WindowsDevices(() => { });
            var samples = await devices.ScanAsync(cancellation.Token);
            var report = samples.Select(item => new { item.Name, Type = item.Kind.ToString(), item.Transport, item.Charge });
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                cancellation.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return 1;
        }
    }
}
