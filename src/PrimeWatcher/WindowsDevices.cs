using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Media.Devices;

namespace PrimeWatcher;

internal sealed class WindowsDevices(Action requestRefresh) : IDisposable
{
    private const string BatteryProperty = "System.Devices.BatteryLife";
    private const string ContainerProperty = "System.Devices.ContainerId";
    private const string EndpointContainerProperty = "System.Devices.Aep.ContainerId";
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string AppearanceProperty = "System.Devices.Aep.Bluetooth.Le.Appearance";
    private const string MajorClassProperty = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string MinorClassProperty = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string BusNameProperty = "System.Devices.BusReportedDeviceDesc";
    private const string InstanceProperty = "System.Devices.DeviceInstanceId";
    private static readonly string[] InterfaceProperties = [ContainerProperty, BatteryProperty, BusNameProperty, InstanceProperty];
    private static readonly string[] EndpointProperties =
        [EndpointContainerProperty, ContainerProperty, AddressProperty, AppearanceProperty,
            MajorClassProperty, MinorClassProperty, BatteryProperty];

    private readonly string classic = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
    private readonly string ble = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
    private readonly string audio = MediaDevice.GetAudioRenderSelector();
    private readonly string[] gamepadSelectors =
    [
        HidDevice.GetDeviceSelector(1, 5),
        HidDevice.GetDeviceSelector(1, 4)
    ];
    private readonly List<DeviceWatcher> watchers = [];
    private readonly Dictionary<string, BluetoothBattery> batteries = new(StringComparer.OrdinalIgnoreCase);

    public void StartWatching()
    {
        Watch(classic, EndpointProperties, DeviceInformationKind.AssociationEndpoint);
        Watch(ble, EndpointProperties, DeviceInformationKind.AssociationEndpoint);
        Watch(audio, InterfaceProperties, DeviceInformationKind.DeviceInterface);
        foreach (string selector in gamepadSelectors)
            Watch(selector, InterfaceProperties, DeviceInformationKind.DeviceInterface);
        ReceiverBatteries.Watch(Watch);
    }

    private void Watch(string selector, string[] properties, DeviceInformationKind kind)
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(selector, properties, kind);
            watcher.Added += (_, _) => requestRefresh();
            watcher.Removed += (_, _) => requestRefresh();
            watcher.Updated += (_, _) => requestRefresh();
            watcher.Start();
            watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
    }

    public async Task<IReadOnlyList<DeviceSample>> ScanAsync(CancellationToken cancellation)
    {
        var gamepadTasks = gamepadSelectors.Select(selector =>
            ReadInterfacesAsync(DeviceKind.Gamepad, selector, cancellation)).ToArray();
        Task<InterfaceDevice[]> audioTask = ReadInterfacesAsync(DeviceKind.Headset, audio, cancellation);
        Task<DeviceInformation[]> classicTask = FindAsync(classic, EndpointProperties, DeviceInformationKind.AssociationEndpoint, cancellation);
        Task<DeviceInformation[]> bleTask = FindAsync(ble, EndpointProperties, DeviceInformationKind.AssociationEndpoint, cancellation);
        Task<ReceiverSnapshot> receiverTask = ReceiverBatteries.ReadAsync(cancellation);

        var interfaces = (await Task.WhenAll(gamepadTasks.Append(audioTask))).SelectMany(items => items)
            .Concat(PnpInputs.Read().Select(item => new InterfaceDevice(
                item.Kind, item.Container, item.Instance, item.Name, item.Percent))).ToArray();
        DeviceInformation[] leEndpoints = await bleTask;
        DeviceInformation[] classicEndpoints = await classicTask;
        ReceiverSnapshot receivers = await receiverTask;
        var samples = receivers.Devices.ToList();
        var xinput = XInput.Read().ToArray();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeBatteries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A dual-mode Bluetooth peripheral can appear as both a Classic and a LE endpoint.
        var endpoints = classicEndpoints.Select(item => (Info: item, IsLe: false))
            .Concat(leEndpoints.Select(item => (Info: item, IsLe: true)));
        var groups = endpoints.GroupBy(item => GroupId(item.Info), StringComparer.OrdinalIgnoreCase);
        var bluetoothTasks = groups.Select(async Task<(DeviceSample Sample, bool IsXInput)?> (group) =>
        {
            var nodes = group.ToArray();
            Guid[] ids = nodes.Select(node => ContainerId(node.Info)).Where(id => id != Guid.Empty).Distinct().ToArray();
            var related = interfaces.Where(item => ids.Contains(item.Container)).ToArray();
            DeviceKind? kind = nodes.Select(node => BluetoothKind(node.Info))
                .FirstOrDefault(value => value.HasValue) ?? related.Select(item => (DeviceKind?)item.Kind).FirstOrDefault();
            if (kind is null)
                return null;

            foreach (InterfaceDevice item in related)
                consumed.Add(item.Key);
            bool xinputAlias = kind == DeviceKind.Gamepad && (related.Any(item => item.IsXInput)
                || nodes.Any(node => node.Info.Name.StartsWith("Xbox ", StringComparison.OrdinalIgnoreCase)));
            var leNode = nodes.FirstOrDefault(node => node.IsLe).Info;
            BluetoothBattery? reader = null;
            if (leNode is not null)
            {
                activeBatteries.Add(leNode.Id);
                if (!batteries.TryGetValue(leNode.Id, out reader))
                    batteries[leNode.Id] = reader = new BluetoothBattery(leNode.Id, requestRefresh);
            }

            var readings = reader is null ? [] : await reader.ReadAsync(cancellation);
            if (readings.Count == 0)
            {
                readings = PercentReading(nodes.Select(node => ReadPercent(node.Info)).Concat(related.Select(item => item.Percent)));
                if (readings.Count == 0 && ids.Length > 0)
                    readings = PercentReading([ReadPercent(await GetContainerAsync(ids[0], cancellation))]);
            }
            string name = nodes.Select(node => node.Info.Name).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? KindName(kind.Value);
            return (new DeviceSample(group.Key, name, kind.Value, "Bluetooth", readings, Wireless: true), xinputAlias);
        }).ToArray();

        var bluetoothResults = (await Task.WhenAll(bluetoothTasks)).OfType<ValueTuple<DeviceSample, bool>>().ToArray();
        foreach (string id in batteries.Keys.Where(id => !activeBatteries.Contains(id)).ToArray())
        {
            batteries[id].Dispose();
            batteries.Remove(id);
        }

        var matchedControllers = new List<DeviceSample>();
        foreach (var (sample, isXInput) in bluetoothResults)
        {
            if (isXInput && xinput.Length > 0)
                matchedControllers.Add(sample);
            else
                samples.Add(sample);
        }

        foreach (var group in interfaces.Where(item => !consumed.Contains(item.Key)
            && !receivers.Handles(item.Container, item.Instance)).GroupBy(item => item.Key))
        {
            InterfaceDevice chosen = group.OrderByDescending(item => item.Percent.HasValue).First();
            var container = chosen.Container == Guid.Empty ? null : await GetContainerAsync(chosen.Container, cancellation);
            string? name = chosen.Name ?? ReadString(container, BusNameProperty) ?? container?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = KindName(chosen.Kind);
            name = ReceiverBatteries.ExplainGenericDevice(chosen.Instance, chosen.Kind, name);
            if (name.Length == 0)
                continue;

            var readings = PercentReading(group.Select(item => item.Percent).Append(ReadPercent(container)));
            bool wireless = chosen.Instance.Contains("BTH", StringComparison.OrdinalIgnoreCase)
                || readings.Count > 0 || LooksWireless(name);
            var sample = new DeviceSample(group.Key, name, chosen.Kind, "HID / Windows", readings, wireless);
            if (chosen.IsXInput && xinput.Length > 0)
                matchedControllers.Add(sample);
            else
                samples.Add(sample);
        }

        // XInput exposes slots without hardware identifiers. Only one candidate and one active
        // slot can be matched unambiguously to give the named controller the fallback level.
        if (matchedControllers.Count == 1 && xinput.Length == 1)
        {
            DeviceSample controller = matchedControllers[0];
            samples.Add(controller.Readings.Count > 0 ? controller : controller with { Readings = xinput[0].Readings });
        }
        else
        {
            samples.AddRange(xinput.Length == matchedControllers.Count && matchedControllers.All(item => item.Readings.Count > 0)
                ? matchedControllers : xinput);
        }

        return samples;
    }

    private async Task<InterfaceDevice[]> ReadInterfacesAsync(DeviceKind kind, string selector, CancellationToken cancellation)
    {
        string[] properties = kind == DeviceKind.Headset ? [.. InterfaceProperties, AudioFormFactor] : InterfaceProperties;
        var items = await FindAsync(selector, properties, DeviceInformationKind.DeviceInterface, cancellation);
        return items.Where(item => kind != DeviceKind.Headset || Number(item, AudioFormFactor) is 3 or 5
                || LooksLikeHeadset(item.Name))
            .Select(info => new InterfaceDevice(kind, ContainerId(info),
                ReadString(info, InstanceProperty) ?? info.Id,
                ReadString(info, BusNameProperty) ?? info.Name, ReadPercent(info)))
            .ToArray();
    }

    // PKEY_AudioEndpoint_FormFactor: Headphones = 3, Headset = 5.
    private const string AudioFormFactor = "{1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E} 0";

    private static bool LooksLikeHeadset(string name) =>
        new[] { "headset", "headphone", "наушник", "гарнитур" }
            .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool LooksWireless(string name) =>
        name.Equals("CIDOO QK75", StringComparison.OrdinalIgnoreCase)
        || new[] { "wireless", "bluetooth", "2.4g", "2,4 г", "беспровод" }
            .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static async Task<DeviceInformation?> GetContainerAsync(Guid id, CancellationToken cancellation)
    {
        try
        {
            return await DeviceInformation.CreateFromIdAsync(id.ToString("B"), [BatteryProperty, BusNameProperty],
                DeviceInformationKind.DeviceContainer).AsTask(cancellation);
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            Trace.WriteLine(ex);
            return null;
        }
    }

    internal static async Task<DeviceInformation[]> FindAsync(string selector, string[] properties,
        DeviceInformationKind kind, CancellationToken cancellation)
    {
        try
        {
            return (await DeviceInformation.FindAllAsync(selector, properties, kind).AsTask(cancellation)).ToArray();
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            Trace.WriteLine(ex);
            return [];
        }
    }

    private static DeviceKind? BluetoothKind(DeviceInformation info)
    {
        int appearance = Number(info, AppearanceProperty) ?? 0;
        int major = Number(info, MajorClassProperty) ?? 0;
        int minor = Number(info, MinorClassProperty) ?? 0;
        if (appearance is 0x03C1 || (major == 5 && (minor & 0x10) != 0))
            return DeviceKind.Keyboard;
        if (appearance is 0x03C2 || (major == 5 && (minor & 0x20) != 0))
            return DeviceKind.Mouse;
        if (appearance is 0x03C3 or 0x03C4 || (major == 5 && (minor & 0x0F) is 1 or 2))
            return DeviceKind.Gamepad;
        if (appearance is >= 0x0940 and <= 0x0944 or 0x0788
            || (major == 4 && minor is 1 or 2 or 6) || LooksLikeHeadset(info.Name))
            return DeviceKind.Headset;
        return null;
    }

    private static string GroupId(DeviceInformation info)
    {
        string? address = ReadString(info, AddressProperty);
        return address is not null && address.Length > 0
            ? $"bluetooth:{address.Replace(":", "").Replace("-", "")}"
            : $"bluetooth:{(ContainerId(info) is var id && id != Guid.Empty ? id.ToString() : info.Id)}";
    }

    internal static Guid ContainerId(DeviceInformation info)
    {
        foreach (string name in new[] { ContainerProperty, EndpointContainerProperty })
        {
            if (!info.Properties.TryGetValue(name, out object? value))
                continue;
            if (value is Guid id && id != Guid.Empty)
                return id;
            if (value is string text && Guid.TryParse(text, out id) && id != Guid.Empty)
                return id;
        }
        return Guid.Empty;
    }

    private static int? ReadPercent(DeviceInformation? info) =>
        Number(info, BatteryProperty) is >= 0 and <= 100 and int value ? value : null;

    private static int? Number(DeviceInformation? info, string property)
    {
        if (info is null || !info.Properties.TryGetValue(property, out object? value))
            return null;
        return value switch { byte n => n, ushort n => n, uint n when n <= int.MaxValue => (int)n, int n => n, _ => null };
    }

    private static string? ReadString(DeviceInformation? info, string property) =>
        info is not null && info.Properties.TryGetValue(property, out object? value) && value is string text
            && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static IReadOnlyList<BatteryReading> PercentReading(IEnumerable<int?> values) =>
        values.FirstOrDefault(value => value.HasValue) is int percent ? [new BatteryReading("", percent)] : [];

    internal static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "Мышь",
        DeviceKind.Keyboard => "Клавиатура",
        DeviceKind.Gamepad => "Геймпад",
        _ => "Наушники"
    };

    public void Dispose()
    {
        foreach (var watcher in watchers)
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
        foreach (BluetoothBattery battery in batteries.Values)
            battery.Dispose();
    }

    private sealed record InterfaceDevice(DeviceKind Kind, Guid Container, string Instance, string? Name, int? Percent)
    {
        public string Key => $"windows:{(Container == Guid.Empty ? Instance : Container.ToString())}:{Kind}";
        public bool IsXInput => Kind == DeviceKind.Gamepad && Instance.Contains("IG_", StringComparison.OrdinalIgnoreCase);
    }
}
