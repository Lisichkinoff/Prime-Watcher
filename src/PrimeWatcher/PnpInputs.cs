using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PrimeWatcher;

internal static class PnpInputs
{
    private static readonly Guid KeyboardClass = new("4D36E96B-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid MouseClass = new("4D36E96F-E325-11CE-BFC1-08002BE10318");
    private static readonly PropertyKey ContainerKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly PropertyKey BusNameKey = new(new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"), 4);
    private static readonly PropertyKey BatteryKey = new(new Guid("49CD1F76-5626-4B17-A4E8-18B4AA1A2213"), 10);

    internal sealed record Endpoint(DeviceKind Kind, string Instance, Guid Container, string? Name, int? Percent);

    public static IEnumerable<Endpoint> Read()
    {
        foreach (var endpoint in ReadClass(KeyboardClass, DeviceKind.Keyboard))
            yield return endpoint;
        foreach (var endpoint in ReadClass(MouseClass, DeviceKind.Mouse))
            yield return endpoint;
    }

    private static IEnumerable<Endpoint> ReadClass(Guid classId, DeviceKind kind)
    {
        using var list = SetupDiGetClassDevs(in classId, null, IntPtr.Zero, flags: 2);
        if (list.IsInvalid)
            yield break;

        for (uint index = 0; ; index++)
        {
            var entry = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            if (!SetupDiEnumDeviceInfo(list, index, ref entry))
                yield break;
            var instance = new StringBuilder(512);
            if (!SetupDiGetDeviceInstanceId(list, ref entry, instance, (uint)instance.Capacity, out _))
                continue;

            Guid container = Container(entry.DevInst);
            string? name = null;
            int? percent = null;
            uint node = entry.DevInst;
            for (int depth = 0; depth < 5; depth++)
            {
                byte[]? label = Property(node, BusNameKey, 512, 18);
                if (label is not null && name is null)
                    name = Encoding.Unicode.GetString(label).TrimEnd('\0');
                byte[]? battery = Property(node, BatteryKey, 1, 3);
                if (battery is [<= 100] && percent is null)
                    percent = battery[0];
                if ((name is not null && percent is not null) || container == Guid.Empty
                    || CM_Get_Parent(out uint parent, node, 0) != 0 || Container(parent) != container)
                    break;
                node = parent;
            }

            yield return new Endpoint(kind, instance.ToString(), container, name, percent);
        }
    }

    private static Guid Container(uint node) => Property(node, ContainerKey, 16, 13) is { Length: 16 } bytes
        ? new Guid(bytes) : Guid.Empty;

    private static byte[]? Property(uint node, PropertyKey key, uint size, uint expectedType)
    {
        var data = new byte[size];
        uint result = CM_Get_DevNode_Property(node, in key, out uint type, data, ref size, 0);
        return result == 0 && type == expectedType ? data[..(int)size] : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid Format, uint Id);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private sealed class DeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceInfoSet() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern DeviceInfoSet SetupDiGetClassDevs(in Guid classGuid, string? enumerator, IntPtr window, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(DeviceInfoSet deviceInfoSet, uint index, ref DeviceInfo item);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(DeviceInfoSet deviceInfoSet, ref DeviceInfo item,
        StringBuilder instance, uint capacity, out uint required);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, in PropertyKey key,
        out uint propertyType, [Out] byte[] buffer, ref uint size, uint flags);
}
