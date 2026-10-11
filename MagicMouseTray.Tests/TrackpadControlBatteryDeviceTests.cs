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
    [InlineData(false, 52u, 4u, 5)]
    [InlineData(true, 52u, 3u, 0)]
    [InlineData(true, 101u, 4u, 0)]
    [InlineData(true, uint.MaxValue, 0u, 0)]
    public void NormalizeRejectsFailedShortOrOutOfRangeResults(
        bool success, uint rawPercent, uint bytesReturned, int errorCode)
    {
        var reply = new TrackpadBatteryControlReply(success, rawPercent, bytesReturned, errorCode);
        Assert.Equal(-2, TrackpadControlBatteryDevice.Normalize(reply));
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

    private sealed class FakeTransport(TrackpadBatteryControlReply reply)
        : ITrackpadBatteryControlTransport
    {
        public TrackpadBatteryControlReply ReadBattery() => reply;
    }
}
