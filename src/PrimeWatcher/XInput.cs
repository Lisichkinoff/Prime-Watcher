using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PrimeWatcher;

internal static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Battery
    {
        public byte Type, Level;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint userIndex, out State state);

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetBatteryInformation(uint userIndex, byte deviceType, out Battery battery);

    public static IEnumerable<DeviceSample> Read()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out _) != 0)
                continue;

            BatteryLevel? level = null;
            if (XInputGetBatteryInformation(slot, 0, out Battery battery) == 0
                && battery.Type is 2 or 3 && battery.Level <= 3)
                level = (BatteryLevel)battery.Level;

            yield return new DeviceSample($"xinput:{slot}", $"Геймпад XInput {slot + 1}", DeviceKind.Gamepad,
                "XInput", level is BatteryLevel value ? [new BatteryReading("", Level: value)] : []);
        }
    }
}
