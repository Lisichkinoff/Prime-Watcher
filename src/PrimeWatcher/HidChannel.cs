using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PrimeWatcher;

internal sealed class HidChannel : IDisposable
{
    private readonly FileStream stream;
    private readonly int inputLength;
    public int OutputLength { get; }

    private HidChannel(SafeFileHandle handle, HidCaps caps)
    {
        stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        inputLength = caps.InputReportByteLength;
        OutputLength = caps.OutputReportByteLength;
    }

    public static HidChannel? Open(string path, int outputLength)
    {
        SafeFileHandle handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out IntPtr data))
                return null;
            try
            {
                if (HidP_GetCaps(data, out HidCaps caps) != 0x00110000
                    || caps.InputReportByteLength is 0 or > 256 || caps.OutputReportByteLength != outputLength)
                    return null;
                var channel = new HidChannel(handle, caps);
                handle = null!;
                return channel;
            }
            finally
            {
                HidD_FreePreparsedData(data);
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public async Task<byte[]?> ExchangeAsync(byte[] command, Func<byte[], int, bool> matches,
        TimeSpan timeout, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var output = new byte[OutputLength];
        command.CopyTo(output, 0);
        var input = new byte[inputLength];
        try
        {
            await stream.WriteAsync(output, deadline.Token);
            while (true)
            {
                int length = await stream.ReadAsync(input, deadline.Token);
                if (length == 0)
                    return null;
                if (matches(input, length))
                    return input[..length];
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => stream.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
}
