using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace PrimeWatcher;

internal sealed record ReceiverSnapshot(IReadOnlyList<DeviceSample> Devices, IReadOnlySet<Guid> HandledContainers)
{
    public bool Handles(Guid container, string instance) =>
        container != Guid.Empty && HandledContainers.Contains(container)
        && !instance.Contains("BTH", StringComparison.OrdinalIgnoreCase);
}

internal static class ReceiverBatteries
{
    private const string ContainerProperty = "System.Devices.ContainerId";
    private static readonly string[] Properties = [ContainerProperty];
    private static readonly Receiver[] Receivers =
    [
        new(0x01C0, 1, 0x03F0, [0x02CC, 0x06BE], ReadHyperXAsync),
        new(0xFF02, 2, 0x3554, [0xF503], ReadVgnAsync),
        new(0xFF00, 1, 0x046D, [0xC52B, 0xC532, 0xC534, 0xC548], ReadLogitechAsync),
        new(0xFF00, 2, 0x046D, [0xC52B, 0xC532, 0xC534, 0xC548], ReadLogitechAsync)
    ];

    public static void Watch(Action<string, string[], DeviceInformationKind> add)
    {
        foreach (Receiver receiver in Receivers)
        foreach (ushort product in receiver.Products)
            add(HidDevice.GetDeviceSelector(receiver.Page, receiver.Usage, receiver.Vendor, product),
                Properties, DeviceInformationKind.DeviceInterface);
    }

    public static async Task<ReceiverSnapshot> ReadAsync(CancellationToken cancellation)
    {
        var tasks = Receivers.SelectMany(receiver => receiver.Products.Select(product =>
            DiscoverAsync(receiver, product, cancellation)));
        var endpoints = (await Task.WhenAll(tasks)).SelectMany(items => items).ToArray();
        var groups = endpoints.GroupBy(item => item.Container == Guid.Empty
            ? item.Info.Id : item.Container.ToString(), StringComparer.OrdinalIgnoreCase);
        ProbeResult[] results = await Task.WhenAll(groups.Select(async group =>
        {
            foreach (var item in group)
            {
                try
                {
                    DeviceSample[]? samples = await item.Receiver.Read(item.Info.Id, group.Key, cancellation);
                    if (samples is not null)
                        return new ProbeResult(item.Container, samples);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                {
                    Trace.WriteLine(ex);
                }
            }
            return new ProbeResult(Guid.Empty, []);
        }));
        return new ReceiverSnapshot(results.SelectMany(result => result.Samples).ToArray(),
            results.Select(result => result.Container).Where(id => id != Guid.Empty).ToHashSet());
    }

    private static async Task<Endpoint[]> DiscoverAsync(Receiver receiver, ushort product, CancellationToken cancellation)
    {
        string selector = HidDevice.GetDeviceSelector(receiver.Page, receiver.Usage, receiver.Vendor, product);
        var items = await WindowsDevices.FindAsync(selector, Properties, DeviceInformationKind.DeviceInterface, cancellation);
        return items.Select(item => new Endpoint(receiver, item, WindowsDevices.ContainerId(item))).ToArray();
    }

    private static async Task<DeviceSample[]?> ReadHyperXAsync(string path, string id, CancellationToken cancellation)
    {
        using var channel = HidChannel.Open(path, 64);
        if (channel is null)
            return null;
        byte[]? response = await channel.ExchangeAsync([0x0C, 0x02, 0x03, 0x01, 0x00, 0x06],
            (report, length) => length >= 7 && report[0] == 0x0C && report[5] == 0x06,
            TimeSpan.FromMilliseconds(750), cancellation);
        if (response is null)
            return null;
        return response[6] <= 100
            ? [new($"windows:{id}:Headset", "HyperX Cloud III S Wireless", DeviceKind.Headset, "USB 2,4 ГГц", [new("", response[6])])]
            : [];
    }

    private static async Task<DeviceSample[]?> ReadVgnAsync(string path, string id, CancellationToken cancellation)
    {
        using var channel = HidChannel.Open(path, 17);
        if (channel is null)
            return null;
        // CompX report 0x08 uses a checksum over the complete frame, including the report ID.
        byte[] command = [0x08, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49];
        byte[]? response = await channel.ExchangeAsync(command, (report, length) => length == 17
            && report[0] == 8 && report[1] == 4 && report[2] == 0 && report[6] <= 100
            && (report.Sum(value => value) & 0xFF) == 0x55, TimeSpan.FromMilliseconds(750), cancellation);
        return response is null ? null
            : [new($"windows:{id}:Mouse", "VGN 2.4G Mouse", DeviceKind.Mouse, "USB 2,4 ГГц", [new("", response[6])])];
    }

    private static async Task<DeviceSample[]?> ReadLogitechAsync(string path, string id, CancellationToken cancellation)
    {
        using var channel = HidChannel.Open(path, 20);
        if (channel is null)
            return null;

        var devices = new List<DeviceSample>();
        for (byte slot = 1; slot <= 6; slot++)
        {
            byte device = slot;
            async Task<byte[]?> Request(byte feature, byte function, byte first = 0, byte second = 0)
            {
                const byte clientId = 0x0D;
                byte address = (byte)((function << 4) | clientId);
                byte[]? response = await channel.ExchangeAsync([0x11, device, feature, address, first, second],
                    (report, length) => length >= 7 && report[0] is 0x10 or 0x11 && report[1] == device
                        && ((report[2] == feature && report[3] == address)
                            || (report[2] is 0x8F or 0xFF && report[3] == feature && report[4] == address)),
                    TimeSpan.FromMilliseconds(220), cancellation);
                return response is not null && response[2] == feature ? response[4..] : null;
            }

            async Task<byte> Feature(ushort number) =>
                (await Request(0, 0, (byte)(number >> 8), (byte)number))?[0] ?? 0;

            byte nameFeature = await Feature(0x0005);
            if (nameFeature == 0)
                continue;
            byte[]? kindResponse = await Request(nameFeature, 2);
            DeviceKind? kind = kindResponse?[0] switch
            {
                0 or 1 or 2 => DeviceKind.Keyboard,
                3 or 7 or 8 => DeviceKind.Mouse,
                6 => DeviceKind.Headset,
                9 => DeviceKind.Gamepad,
                _ => null
            };
            if (kind is null)
                continue;

            int nameLength = Math.Min((await Request(nameFeature, 0))?[0] ?? 0, (byte)80);
            var nameBytes = new List<byte>();
            while (nameBytes.Count < nameLength)
            {
                var chunk = await Request(nameFeature, 1, (byte)nameBytes.Count);
                if (chunk is null)
                    break;
                nameBytes.AddRange(chunk.Take(nameLength - nameBytes.Count));
            }
            string name = Encoding.UTF8.GetString(nameBytes.ToArray()).Trim('\0', ' ');
            if (name.Length == 0)
                name = $"Logitech · {WindowsDevices.KindName(kind.Value)} {slot}";

            BatteryReading? reading = null;
            byte unified = await Feature(0x1004);
            if (unified != 0)
            {
                byte[]? capabilities = await Request(unified, 0);
                byte[]? status = await Request(unified, 1);
                if (status is not null && capabilities is not null)
                {
                    if ((capabilities[1] & 2) != 0 && status[0] <= 100)
                        reading = new("", status[0]);
                    else if (status[1] is 1 or 2 or 4 or 8)
                        reading = new("", Level: status[1] switch
                        {
                            8 => BatteryLevel.Full,
                            4 => BatteryLevel.Medium,
                            _ => BatteryLevel.Low
                        });
                }
            }
            if (reading is null && await Feature(0x1000) is var standard && standard != 0
                && await Request(standard, 0) is { } battery && battery[0] <= 100)
                reading = new("", battery[0]);

            devices.Add(new DeviceSample($"receiver:{id}:{slot}:{name}", name, kind.Value, "Logitech USB 2,4 ГГц",
                reading is null ? [] : [reading]));
        }
        return devices.Count == 0 ? null : devices.ToArray();
    }

    public static string ExplainGenericDevice(string instance, DeviceKind kind, string name)
    {
        if (instance.Contains("VID_046D&PID_C231", StringComparison.OrdinalIgnoreCase)
            || instance.Contains("VID_046D&PID_C232", StringComparison.OrdinalIgnoreCase))
            return "";
        if (instance.Contains("VID_3554&PID_F503", StringComparison.OrdinalIgnoreCase))
            return kind == DeviceKind.Mouse ? "VGN 2.4G Mouse" : "";
        if (instance.Contains("VID_36B0&PID_30CC", StringComparison.OrdinalIgnoreCase))
            return kind == DeviceKind.Keyboard ? "CIDOO QK75" : "";
        return name;
    }

    private sealed record Receiver(ushort Page, ushort Usage, ushort Vendor, ushort[] Products,
        Func<string, string, CancellationToken, Task<DeviceSample[]?>> Read);
    private sealed record Endpoint(Receiver Receiver, DeviceInformation Info, Guid Container);
    private sealed record ProbeResult(Guid Container, DeviceSample[] Samples);
}
