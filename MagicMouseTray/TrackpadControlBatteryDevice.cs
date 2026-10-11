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
    readonly bool _allowBrokerFallback;

    internal TrackpadControlBatteryDevice()
        : this(new Win32TrackpadBatteryControlTransport(), allowBrokerFallback: true)
    {
    }

    internal TrackpadControlBatteryDevice(ITrackpadBatteryControlTransport transport)
        : this(transport, allowBrokerFallback: false)
    {
    }

    TrackpadControlBatteryDevice(
        ITrackpadBatteryControlTransport transport,
        bool allowBrokerFallback)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _allowBrokerFallback = allowBrokerFallback;
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

        // The upstream control device is ACL-restricted to Administrators and
        // SYSTEM. Keep the tray unelevated: only after Access Denied, ask the
        // separate opt-in elevated broker for this fixed read-only operation.
        if (!reply.Success && reply.ErrorCode == 5 && _allowBrokerFallback &&
            TrackpadBatteryBroker.TryReadBattery(out var brokerReply))
        {
            reply = brokerReply;
        }

        var normalized = Normalize(reply);
        if (normalized >= 0)
            Logger.Log($"TRACKPAD_CONTROL_BATTERY_OK pct={normalized}%");
        else
            Logger.Log($"TRACKPAD_CONTROL_BATTERY_UNAVAILABLE win32={reply.ErrorCode} bytes={reply.BytesReturned}");

        return normalized;
    }

    // Returns 0..100 only for a successful, complete, valid ULONG result.
    // Missing control device (Win32 2/3) is -1 so it does not override another
    // provider's result. Other failures are -2 ("present but unreadable/blocked").
    // The existing direct HID provider remains in place and wins if it returns
    // a real percentage.
    internal static int Normalize(TrackpadBatteryControlReply reply)
    {
        if (!reply.Success)
            return reply.ErrorCode is 2 or 3 ? -1 : -2;
        if (reply.BytesReturned < sizeof(uint) || reply.Percent > 100)
            return -2;

        return (int)reply.Percent;
    }

    // Called only inside the elevated broker process; it performs the same fixed
    // GET_BATTERY query without trying to connect back to the unelevated tray.
    internal static TrackpadBatteryControlReply ReadBatteryReplyDirect() =>
        new Win32TrackpadBatteryControlTransport().ReadBattery();

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
