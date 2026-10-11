// SPDX-License-Identifier: MIT
// Narrow, opt-in bridge for the administrator-restricted AmtPtp control device.
// The unelevated tray can request only a Magic Trackpad 2 battery percentage;
// no arbitrary IOCTLs or device writes are accepted by this pipe.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace MagicMouseTray;

internal static class TrackpadBatteryBroker
{
    internal const string BrokerArgument = "--trackpad-battery-broker";
    const string PipeName = "OwnMagicUtilities.TrackpadBattery.v1";
    const string BatteryRequest = "GET_BATTERY_0265";
    const string PingRequest = "PING";

    internal static bool IsRunning()
    {
        if (!TryRequest(PingRequest, out string? response))
            return false;

        return string.Equals(response, "PONG", StringComparison.Ordinal);
    }

    internal static bool TryReadBattery(out TrackpadBatteryControlReply reply)
    {
        reply = new TrackpadBatteryControlReply(false, 0, 0, 2);
        if (!TryRequest(BatteryRequest, out string? response))
            return false;

        return TryParseBatteryResponse(response, out reply);
    }

    internal static bool TryParseBatteryResponse(
        string? response,
        out TrackpadBatteryControlReply reply)
    {
        reply = new TrackpadBatteryControlReply(false, 0, 0, 87);
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var parts = response.Split('|');
        if (parts.Length == 2 &&
            parts[0] == "OK" &&
            uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint pct) &&
            pct <= 100)
        {
            reply = new TrackpadBatteryControlReply(true, pct, sizeof(uint), 0);
            return true;
        }

        if (parts.Length == 2 &&
            parts[0] == "ERR" &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int error) &&
            error >= 0)
        {
            reply = new TrackpadBatteryControlReply(false, 0, 0, error);
            return true;
        }

        return false;
    }

    static bool TryRequest(string request, out string? response)
    {
        response = null;
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.None);

            pipe.Connect(350);
            pipe.ReadTimeout = 2000;
            pipe.WriteTimeout = 1000;

            using var reader = new StreamReader(
                pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024, leaveOpen: true);
            using var writer = new StreamWriter(
                pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true
            };

            writer.WriteLine(request);
            response = reader.ReadLine();
            return response is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryStartElevated(out string message)
    {
        if (IsRunning())
        {
            message = "The Trackpad battery bridge is already running.";
            return true;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            message = "Could not locate the Own Magic Utilities executable to start the battery bridge.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = BrokerArgument,
                UseShellExecute = true,
                Verb = "runas"
            });

            message = "The elevated battery bridge was requested. Approve the Windows prompt, then choose Refresh Now.";
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            message = "Windows elevation was cancelled. The app and device drivers were not changed.";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Could not start the battery bridge: {ex.GetBaseException().Message}";
            return false;
        }
    }

    internal static async Task RunServerAsync(CancellationToken cancellationToken)
    {
        Logger.Log("TRACKPAD_BATTERY_BROKER_START");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(
                    server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(
                    server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                string? request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                string response = request switch
                {
                    PingRequest => "PONG",
                    BatteryRequest => GetBatteryResponse(),
                    _ => "ERR|87"
                };

                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                Logger.Log($"TRACKPAD_BATTERY_BROKER_PIPE_ERROR err={ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Log($"TRACKPAD_BATTERY_BROKER_ERROR err={ex.GetBaseException().Message}");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        Logger.Log("TRACKPAD_BATTERY_BROKER_STOP");
    }

    static string GetBatteryResponse()
    {
        var reply = TrackpadControlBatteryDevice.ReadBatteryReplyDirect();
        int percent = TrackpadControlBatteryDevice.Normalize(reply);
        if (percent >= 0)
            return $"OK|{percent}";

        // Keep this protocol intentionally small. A broker failure is a status
        // result, not permission to fall back to another operation.
        int error = reply.ErrorCode != 0 ? reply.ErrorCode : 87;
        return $"ERR|{error}";
    }
}
