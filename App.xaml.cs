#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using RemotePlayServer.Core;
using RemotePlayServer.Infrastructure.Display;
using RemotePlayServer.Services;
using RemotePlayServer.ViewModels;
using RemotePlayServer.Views;

namespace RemotePlayServer;

public partial class App : System.Windows.Application
{
    private ServerService? _serverService;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Logger.InitFileLogging();

        SetupExceptionHandlers();

        _serverService = new ServerService();
        var mainVm = new MainViewModel(_serverService);
        MainWindow = new MainWindow { DataContext = mainVm };
        MainWindow.Show();

        // Run entire startup on thread pool — Dispatch() marshals UI updates back
        _ = Task.Run(async () =>
        {
            await _serverService.StartAsync();
            // Try auto-login with saved credentials after server is ready
            await mainVm.TryAutoLoginAsync();
        });
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_serverService != null)
        {
            await _serverService.StopAsync();
            _serverService.Dispose();
        }

        try { DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(5)); }
        catch { }

        Logger.Shutdown();
        base.OnExit(e);
    }

    private void SetupExceptionHandlers()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var crashLogPath = Path.Combine(logDir, "crash.log");

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNHANDLED EXCEPTION (IsTerminating={args.IsTerminating}):\n{ex}\n\n";
            try { File.AppendAllText(crashLogPath, msg); } catch { }

            if (args.IsTerminating)
            {
                try { DisplayGuard.RestoreAndCleanupWithTimeout(TimeSpan.FromSeconds(10)); } catch { }
            }
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            if (args.Exception.InnerException is System.Net.Sockets.SocketException sockEx
                && sockEx.NativeErrorCode == 995)
            {
                args.SetObserved();
                return;
            }

            if (args.Exception.InnerException is ArgumentOutOfRangeException outOfRange
                && outOfRange.StackTrace?.Contains("SIPSorcery.Net.ChecklistEntry.GotStunResponse") == true)
            {
                Logger.Debug($"[SIPSorcery] Ignored internal race condition: {outOfRange.Message}");
                args.SetObserved();
                return;
            }

            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNOBSERVED TASK EXCEPTION:\n{args.Exception}\n\n";
            try { File.AppendAllText(crashLogPath, msg); } catch { }
            args.SetObserved();
        };

        DispatcherUnhandledException += (sender, args) =>
        {
            Logger.Error($"[WPF] Dispatcher exception: {args.Exception.Message}", args.Exception);
            var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] WPF DISPATCHER EXCEPTION:\n{args.Exception}\n\n";
            try { File.AppendAllText(crashLogPath, msg); } catch { }
#if DEBUG
            args.Handled = false;
#else
            args.Handled = true;
#endif
        };
    }
}
