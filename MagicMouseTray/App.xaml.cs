// SPDX-License-Identifier: MIT
using System.Windows;

namespace MagicMouseTray;

public partial class App
{
    TrayApp? _trayApp;
    static Mutex? _instanceMutex;
    static bool _ownsMutex;
    static Mutex? _brokerMutex;
    static bool _ownsBrokerMutex;
    CancellationTokenSource? _brokerCancellation;
    Task? _brokerTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The broker reuses this signed/published executable but runs in a separate,
        // explicitly elevated process. It does not create a tray icon or initialize
        // the normal app; its only IPC command returns Trackpad 2 battery status.
        if (Array.Exists(e.Args, arg =>
                string.Equals(arg, TrackpadBatteryBroker.BrokerArgument, StringComparison.Ordinal)))
        {
            StartBatteryBroker();
            return;
        }

        // Single-instance guard. Local scope is correct for a per-user tray app.
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\MagicMouseTray.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            _instanceMutex.Dispose();   // not owned — release handle, do NOT ReleaseMutex
            _instanceMutex = null;
            System.Windows.MessageBox.Show(
                "Magic Tray is already running",
                "Magic Tray",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Logger.LogAppStart();

        System.AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            if (ev.ExceptionObject is System.Exception ex)
                Logger.Log($"FATAL_ERROR AppDomain: {ex.Message}\n{ex.StackTrace}");
            else
                Logger.Log("FATAL_ERROR AppDomain: Unknown exception");
        };

        DispatcherUnhandledException += (s, ev) =>
        {
            Logger.Log($"ERROR Dispatcher: {ev.Exception.Message}\n{ev.Exception.StackTrace}");
            ev.Handled = true;
        };

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _trayApp = new TrayApp(Config.Load());
    }

    void StartBatteryBroker()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _brokerMutex = new Mutex(
            initiallyOwned: true,
            @"Local\OwnMagicUtilities.TrackpadBatteryBroker",
            out _ownsBrokerMutex);

        if (!_ownsBrokerMutex)
        {
            _brokerMutex.Dispose();
            _brokerMutex = null;
            Shutdown();
            return;
        }

        _brokerCancellation = new CancellationTokenSource();
        _brokerTask = TrackpadBatteryBroker.RunServerAsync(_brokerCancellation);
        _ = _brokerTask.ContinueWith(
            _ =>
            {
                try { Dispatcher.BeginInvoke(new Action(Shutdown)); }
                catch (InvalidOperationException) { }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // When the user quits the unelevated tray, stop the same-user elevated
        // broker through its fixed STOP command rather than leaving it running.
        if (_ownsMutex)
            TrackpadBatteryBroker.StopIfRunning();

        _brokerCancellation?.Cancel();
        try { _brokerTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _brokerCancellation?.Dispose();

        _trayApp?.Dispose();
        if (_ownsMutex)                  // only the owner releases + disposes
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        if (_ownsBrokerMutex)
        {
            _brokerMutex?.ReleaseMutex();
            _brokerMutex?.Dispose();
        }
        base.OnExit(e);
    }
}
