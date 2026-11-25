using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace PrimeWatcher;

internal sealed class BluetoothBattery(string endpointId, Action onChanged) : IDisposable
{
    private static readonly Guid BatteryService = BluetoothUuidHelper.FromShortId(0x180F);
    private static readonly Guid BatteryLevelCharacteristic = BluetoothUuidHelper.FromShortId(0x2A19);
    private readonly List<GattDeviceService> services = [];
    private readonly List<BatteryPart> parts = [];
    private BluetoothLEDevice? device;
    private DateTimeOffset nextDiscovery;
    private int reconnected;

    public async Task<IReadOnlyList<BatteryReading>> ReadAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref reconnected, 0) != 0)
        {
            Dispose();
            nextDiscovery = DateTimeOffset.MinValue;
        }

        if (parts.Count == 0 && DateTimeOffset.UtcNow >= nextDiscovery)
        {
            nextDiscovery = DateTimeOffset.UtcNow.AddMinutes(1);
            try
            {
                await DiscoverAsync(cancellation);
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested)
            {
                Trace.WriteLine(ex);
            }
        }

        await Task.WhenAll(parts.Select(part => part.PollAsync(cancellation)));
        if (parts.Count == 0 || parts.All(part => part.Reading.Percent is null))
            return [];

        return parts.Select(part => part.Reading).ToArray();
    }

    private async Task DiscoverAsync(CancellationToken cancellation)
    {
        Dispose();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken token = deadline.Token;
        device = await BluetoothLEDevice.FromIdAsync(endpointId).AsTask(token);
        if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            return;

        device.ConnectionStatusChanged += ConnectionChanged;
        var result = await device.GetGattServicesForUuidAsync(BatteryService, BluetoothCacheMode.Cached).AsTask(token);
        if (result.Status != GattCommunicationStatus.Success)
            return;

        services.AddRange(result.Services);
        var characteristics = new List<GattCharacteristic>();
        foreach (GattDeviceService service in services)
        {
            var found = await service.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristic, BluetoothCacheMode.Cached).AsTask(token);
            if (found.Status == GattCommunicationStatus.Success)
                characteristics.AddRange(found.Characteristics);
        }

        for (int index = 0; index < characteristics.Count; index++)
        {
            GattCharacteristic characteristic = characteristics[index];
            parts.Add(new BatteryPart(characteristic,
                Label(characteristic, characteristics.Count, index + 1), onChanged));
        }
        await Task.WhenAll(parts.Select(part => part.SubscribeAsync(token)));
    }

    private void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        Interlocked.Exchange(ref reconnected, 1);
        onChanged();
    }

    private static string Label(GattCharacteristic characteristic, int count, int number)
    {
        string description = characteristic.UserDescription.Trim().ToLowerInvariant();
        if (description.Contains("case", StringComparison.Ordinal) || description.Contains("кейс", StringComparison.Ordinal))
            return "Кейс";
        if (description.Contains("left", StringComparison.Ordinal) || description.Contains("лев", StringComparison.Ordinal))
            return "Левый";
        if (description.Contains("right", StringComparison.Ordinal) || description.Contains("прав", StringComparison.Ordinal))
            return "Правый";

        var format = characteristic.PresentationFormats.FirstOrDefault(item => item.Namespace == 1);
        return format?.Description switch
        {
            0x010D => "Левый",
            0x010E => "Правый",
            0x0106 when count > 1 => "Основной",
            _ => count == 1 ? "" : $"Батарея {number}"
        };
    }

    public void Dispose()
    {
        foreach (BatteryPart part in parts)
            part.Dispose();
        parts.Clear();
        foreach (GattDeviceService service in services)
            service.Dispose();
        services.Clear();
        if (device is not null)
        {
            device.ConnectionStatusChanged -= ConnectionChanged;
            device.Dispose();
            device = null;
        }
    }

    private sealed class BatteryPart(GattCharacteristic characteristic, string label, Action onChanged) : IDisposable
    {
        private readonly object gate = new();
        private bool subscribed;
        private int? percent;
        private DateTimeOffset? observedAt;
        private int revision;
        private DateTimeOffset lastPoll;

        public BatteryReading Reading
        {
            get
            {
                lock (gate)
                    return new BatteryReading(label, percent, ObservedAt: observedAt);
            }
        }

        public async Task SubscribeAsync(CancellationToken cancellation)
        {
            var properties = characteristic.CharacteristicProperties;
            var mode = properties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : properties.HasFlag(GattCharacteristicProperties.Indicate)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                    : GattClientCharacteristicConfigurationDescriptorValue.None;
            if (mode == GattClientCharacteristicConfigurationDescriptorValue.None)
                return;

            characteristic.ValueChanged += ValueChanged;
            try
            {
                subscribed = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode)
                    .AsTask(cancellation) == GattCommunicationStatus.Success;
            }
            finally
            {
                if (!subscribed)
                    characteristic.ValueChanged -= ValueChanged;
            }
        }

        public async Task PollAsync(CancellationToken cancellation)
        {
            if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)
                || DateTimeOffset.UtcNow - lastPoll < TimeSpan.FromSeconds(subscribed ? 120 : 12))
                return;

            lastPoll = DateTimeOffset.UtcNow;
            int previousRevision;
            lock (gate)
                previousRevision = revision;

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(4));
                var response = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(deadline.Token);
                lock (gate)
                {
                    if (revision == previousRevision)
                    {
                        percent = response.Status == GattCommunicationStatus.Success ? Decode(response.Value) : null;
                        observedAt = percent.HasValue ? DateTimeOffset.Now : null;
                    }
                }
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested)
            {
                Trace.WriteLine(ex);
                lock (gate)
                {
                    if (revision == previousRevision)
                        percent = null;
                }
            }
        }

        private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            int? reading = Decode(args.CharacteristicValue);
            if (!reading.HasValue)
                return;
            lock (gate)
            {
                revision++;
                percent = reading;
                observedAt = DateTimeOffset.Now;
            }
            onChanged();
        }

        private static int? Decode(IBuffer? data)
        {
            if (data?.Length != 1)
                return null;
            using var reader = DataReader.FromBuffer(data);
            byte value = reader.ReadByte();
            return value <= 100 ? value : null;
        }

        public void Dispose()
        {
            if (subscribed)
                characteristic.ValueChanged -= ValueChanged;
        }
    }
}
