using System;
using System.Collections.Generic;
using System.Linq;

namespace PrimeWatcher;

internal enum DeviceKind { Headset, Mouse, Keyboard, Gamepad }
internal enum BatteryLevel { Empty, Low, Medium, Full }

internal sealed record BatteryReading(string Label, int? Percent = null, BatteryLevel? Level = null,
    DateTimeOffset? ObservedAt = null)
{
    public string Text => Percent is int percent ? $"{percent}%" : Level switch
    {
        BatteryLevel.Empty => "Разряжен",
        BatteryLevel.Low => "Низкий заряд",
        BatteryLevel.Medium => "Средний заряд",
        BatteryLevel.Full => "Полный заряд",
        _ => "Заряд недоступен"
    };
}

internal sealed record DeviceSample(string Id, string Name, DeviceKind Kind, string Transport,
    IReadOnlyList<BatteryReading> Readings, bool Wireless = false)
{
    public string Charge => FormatCharge(Readings);

    public static string FormatCharge(IReadOnlyList<BatteryReading> readings) => readings.Count == 0
        ? "Заряд недоступен"
        : string.Join(" · ", readings.Select(value =>
            value.Label.Length == 0 ? value.Text : $"{value.Label} {value.Text}"));
}

internal sealed record DeviceState(DeviceSample Device, bool Connected,
    IReadOnlyList<BatteryReading> LastReadings, DateTimeOffset? ObservedAt)
{
    public string Detail => Connected
        ? $"Подключено · {Device.Transport}"
        : ObservedAt is DateTimeOffset time
            ? $"Отключено · {time.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "Отключено";

    public string Charge => Connected ? Device.Charge : LastReadings.Count > 0
        ? $"Последний заряд · {DeviceSample.FormatCharge(LastReadings)}"
        : "Заряд недоступен";
}

internal sealed class DeviceHistory
{
    private readonly Dictionary<string, DeviceState> states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceState> Update(IEnumerable<DeviceSample> samples, DateTimeOffset now)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DeviceSample sample in samples)
        {
            present.Add(sample.Id);
            states.TryGetValue(sample.Id, out DeviceState? previous);
            var observed = sample.Readings.Where(value => value.Percent.HasValue || value.Level.HasValue)
                .Select(value => (DateTimeOffset?)(value.ObservedAt ?? now)).Max();
            states[sample.Id] = new DeviceState(sample, true,
                observed.HasValue ? sample.Readings : previous?.LastReadings ?? [],
                observed ?? previous?.ObservedAt);
        }

        foreach (string id in states.Keys.Where(id => !present.Contains(id)).ToArray())
            states[id] = states[id] with { Connected = false };

        return states.Values.OrderByDescending(value => value.Connected)
            .ThenBy(value => value.Device.Kind).ThenBy(value => value.Device.Name).ToArray();
    }
}
