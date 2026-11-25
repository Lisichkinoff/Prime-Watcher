using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PrimeWatcher;

internal sealed class DeviceMonitor : IDisposable
{
    private readonly DeviceHistory history = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refresh = new(0, 1);
    private readonly WindowsDevices devices;

    public event Action<IReadOnlyList<DeviceState>>? Changed;

    public DeviceMonitor()
    {
        devices = new WindowsDevices(RequestRefresh);
    }

    public void Start() => _ = Task.Run(RunAsync);

    public void RequestRefresh()
    {
        if (lifetime.IsCancellationRequested)
            return;
        try { refresh.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            devices.StartWatching();
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await devices.ScanAsync(lifetime.Token);
                    Changed?.Invoke(history.Update(snapshot, DateTimeOffset.Now));
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested)
                {
                    Trace.WriteLine(ex);
                }

                await refresh.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            devices.Dispose();
        }
    }

    public void Dispose() => lifetime.Cancel();
}
