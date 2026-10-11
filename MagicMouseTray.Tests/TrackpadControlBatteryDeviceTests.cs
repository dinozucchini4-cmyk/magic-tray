// SPDX-License-Identifier: MIT
using MagicMouseTray;
using Xunit;

namespace MagicMouseTray.Tests;

public class TrackpadControlBatteryDeviceTests
{
    [Fact]
    public void IoctlMatchesUpstreamControlDeviceDefinition()
    {
        // CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
        Assert.Equal(0x00222004u, TrackpadControlBatteryDevice.IoctlGetBattery);
        Assert.Equal(@"\\.\AmtPtpControlDeviceUm", TrackpadControlBatteryDevice.ControlDevicePath);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(52u, 52)]
    [InlineData(100u, 100)]
    public void NormalizeAcceptsValidCompleteBatteryResult(uint rawPercent, int expected)
    {
        var reply = new TrackpadBatteryControlReply(true, rawPercent, sizeof(uint), 0);
        Assert.Equal(expected, TrackpadControlBatteryDevice.Normalize(reply));
    }

    [Theory]
    [InlineData(false, 52u, 0u, 2, -1)] // control device is absent
    [InlineData(false, 52u, 0u, 3, -1)] // path is absent
    [InlineData(false, 52u, 0u, 5, -2)] // access denied
    [InlineData(true, 52u, 3u, 0, -2)] // truncated result
    [InlineData(true, 101u, 4u, 0, -2)] // impossible percentage
    [InlineData(true, uint.MaxValue, 0u, 0, -2)]
    public void NormalizeRejectsFailedShortOrOutOfRangeResults(
        bool success, uint rawPercent, uint bytesReturned, int errorCode, int expected)
    {
        var reply = new TrackpadBatteryControlReply(success, rawPercent, bytesReturned, errorCode);
        Assert.Equal(expected, TrackpadControlBatteryDevice.Normalize(reply));
    }

    [Fact]
    public void DeviceUsesTrackpad2Identity()
    {
        var device = new TrackpadControlBatteryDevice(new FakeTransport(
            new TrackpadBatteryControlReply(true, 84, sizeof(uint), 0)));

        Assert.Equal("Magic Trackpad 2", device.DeviceName);
        Assert.Equal("0265", device.Pid);
        Assert.Equal(DeviceKind.MagicTrackpadV2, device.Kind);
        Assert.Equal(84, device.GetBatteryPercent());
    }

    [Fact]
    public void DiscoveryAddsControlCandidateOnlyWhenTrackpad2IsPresent()
    {
        IBatteryDevice mouse = new MouseBatteryDevice(
            @"\\?\HID#VID_05AC&PID_0269#mouse",
            "Magic Mouse v2",
            DeviceKind.MagicMouseV2);

        var mouseOnly = DeviceRegistry.AddTrackpadControlProviderIfPresent([mouse]);
        Assert.Single(mouseOnly);

        IBatteryDevice trackpad = new MouseBatteryDevice(
            @"\\?\HID#VID_05AC&PID_0265#trackpad",
            "Magic Trackpad 2",
            DeviceKind.MagicTrackpadV2);

        var withTrackpad = DeviceRegistry.AddTrackpadControlProviderIfPresent([trackpad]);

        Assert.Equal(2, withTrackpad.Count);
        Assert.Equal("Magic Trackpad 2", withTrackpad[1].DeviceName);
        Assert.Equal("0265", withTrackpad[1].Pid);
        Assert.Equal(DeviceKind.MagicTrackpadV2, withTrackpad[1].Kind);
    }

    [Fact]
    public void DiscoverRecognizesObservedBleTrackpad2Path()
    {
        const string path = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&0265&col02#9&2ac49406&4&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        var found = DeviceRegistry.DiscoverFromPaths([path]);

        Assert.Single(found);
        Assert.Equal(DeviceKind.MagicTrackpadV2, found[0].Kind);
        Assert.Equal("0265", found[0].Pid);
        Assert.Equal("Magic Trackpad 2", found[0].DeviceName);
    }

    private sealed class FakeTransport(TrackpadBatteryControlReply reply)
        : ITrackpadBatteryControlTransport
    {
        public TrackpadBatteryControlReply ReadBattery() => reply;
    }
}
