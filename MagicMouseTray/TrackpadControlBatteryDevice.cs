// SPDX-License-Identifier: MIT
// Optional battery path for the open-source AmtPtp Precision Touchpad driver.
// This calls the driver's documented GET_BATTERY control IOCTL only. It does not
// install the driver, change filters, or issue HID feature/output writes.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicMouseTray;

internal readonly record struct TrackpadBatteryControlReply(
    bool Success,
    uint Percent,
    uint BytesReturned,
    int ErrorCode);

internal interface ITrackpadBatteryControlTransport
{
    TrackpadBatteryControlReply ReadBattery();
}

internal sealed class TrackpadControlBatteryDevice : IBatteryDevice
{
    // Defined by MagicTrackpad2ForWindows/AmtPtpHidFilter/include/ControlDevice.h:
    // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS).
    // The driver implementation reads HID input report 0x90 and returns byte 2.
    internal const uint IoctlGetBattery = 0x00222004;
    internal const string ControlDevicePath = @"\\.\AmtPtpControlDeviceUm";

    readonly ITrackpadBatteryControlTransport _transport;

    internal TrackpadControlBatteryDevice()
        : this(new Win32TrackpadBatteryControlTransport())
    {
    }

    internal TrackpadControlBatteryDevice(ITrackpadBatteryControlTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string DeviceName => "Magic Trackpad 2";
    public string Pid => "0265";
    public DeviceKind Kind => DeviceKind.MagicTrackpadV2;

    public int GetBatteryPercent()
    {
        TrackpadBatteryControlReply reply;
        try
        {
            reply = _transport.ReadBattery();
        }
        catch (Exception ex)
        {
            Logger.Log($"TRACKPAD_CONTROL_BATTERY_ERROR err={ex.GetBaseException().Message}");
            return -2;
        }

        var normalized = Normalize(reply);
        if (normalized >= 0)
            Logger.Log($"TRACKPAD_CONTROL_BATTERY_OK pct={normalized}%");
        else
            Logger.Log($"TRACKPAD_CONTROL_BATTERY_UNAVAILABLE win32={reply.ErrorCode} bytes={reply.BytesReturned}");

        return normalized;
    }

    // Returns 0..100 only for a successful, complete, valid ULONG result.
    // -2 means the Trackpad is known to be present, but this alternate report
    // path is unavailable/blocked. DeviceRegistry adds this provider only when
    // the PID 0265 HID device is present; the existing direct HID provider
    // remains in place and wins if it returns a real percentage.
    internal static int Normalize(TrackpadBatteryControlReply reply)
    {
        if (!reply.Success || reply.BytesReturned < sizeof(uint) || reply.Percent > 100)
            return -2;

        return (int)reply.Percent;
    }

    private sealed class Win32TrackpadBatteryControlTransport : ITrackpadBatteryControlTransport
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr inputBuffer,
            uint inputBufferSize,
            [Out] byte[] outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        public TrackpadBatteryControlReply ReadBattery()
        {
            using SafeFileHandle handle = CreateFile(
                ControlDevicePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                return new TrackpadBatteryControlReply(false, 0, 0, error);
            }

            byte[] output = new byte[sizeof(uint)];
            bool ok = DeviceIoControl(
                handle,
                IoctlGetBattery,
                IntPtr.Zero,
                0,
                output,
                (uint)output.Length,
                out uint bytesReturned,
                IntPtr.Zero);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                return new TrackpadBatteryControlReply(false, 0, bytesReturned, error);
            }

            uint percent = bytesReturned >= sizeof(uint)
                ? BitConverter.ToUInt32(output, 0)
                : uint.MaxValue;

            return new TrackpadBatteryControlReply(true, percent, bytesReturned, 0);
        }
    }
}
