using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RatScanner.Runtime;
using RatScanner.View;
using SingleInstanceCore;

namespace RatScanner;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application, ISingleInstance
{
    private static readonly TimeSpan WebViewDownloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WebViewInstallTimeout = TimeSpan.FromMinutes(10);

    protected override void OnStartup(StartupEventArgs e)
    {
        // First timed point in the process: everything after this is attributable.
        Diagnostics.PerfTrace startup = Diagnostics.PerfTraceStore.Startup;
        startup.Mark("startup.on_startup_enter");

        // Setup single instance mode
        bool isFirstInstance;
        using (startup.Measure("startup.single_instance"))
            isFirstInstance = this.InitializeAsFirstInstance(RatConfig.SINGLE_INSTANCE_GUID);
        if (!isFirstInstance)
        {
            SingleInstance.Cleanup();
            Application.Current.Shutdown(2);
            return;
        }

        using (startup.Measure("startup.splash_show"))
            new SplashScreen("Resources\\RatLogoMedium.png").Show(true, true);
        base.OnStartup(e);

        // Set current working directory to executable location
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

        // Clear the previous-session log before any deferred service can start.
        // RatScannerMain is intentionally lazy now, so log ownership cannot depend
        // on whether the main or overlay WebView resolves it first.
        Logger.Clear();

#if !DEBUG
        SetupExceptionHandling();
#endif

        // Install off the WPF dispatcher so a stalled download/installer cannot freeze the UI pump.
        // Still block startup (UI cannot run without WebView2), but do the work on a worker thread.
        bool webViewAvailable;
        using (startup.Measure("startup.webview2_probe"))
            webViewAvailable = IsWebView2RuntimeAvailable();
        if (!webViewAvailable && !Task.Run(InstallWebView2Runtime).GetAwaiter().GetResult())
        {
            MessageBox.Show(
                "RatScanner requires the Microsoft Edge WebView2 Runtime. Automatic installation failed. "
                    + "Check your internet connection, install WebView2, and start RatScanner again.",
                "RatScanner startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            Shutdown(3);
            return;
        }

        startup.Mark("startup.on_startup_exit");
    }

    public void OnInstanceInvoked(string[] args)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (args.Length > 1)
            {
                OnInstanceInvokedWithArgs(args);
                return;
            }

            Window? mainWindow = Application.Current.MainWindow;
            if (mainWindow is null)
                return;

            mainWindow.Activate();
            mainWindow.WindowState = WindowState.Normal;

            // Invert the topmost state twice to bring
            // the window on top but keep the top most state
            mainWindow.Topmost = !mainWindow.Topmost;
            mainWindow.Topmost = !mainWindow.Topmost;
        });
    }

    public void OnInstanceInvokedWithArgs(string[] args)
    {
        Action action = args[1] switch
        {
            "/showUI" => PageSwitcher.Instance.ShowUI,
            "/showMinimalUI" => PageSwitcher.Instance.ShowMinimalUI,
            _ => () => OnInstanceInvoked(Array.Empty<string>()),
        };
        action.Invoke();
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    private static bool InstallWebView2Runtime()
    {
        string installerPath = Path.Combine(Path.GetTempPath(), $"RatScanner-WebView2-{Guid.NewGuid():N}.exe");

        try
        {
            using HttpClient client = new() { Timeout = WebViewDownloadTimeout };
            byte[] installerBytes = client
                .GetByteArrayAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                .GetAwaiter()
                .GetResult();
            File.WriteAllBytes(installerPath, installerBytes);

            ProcessStartInfo startInfo = new()
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                FileName = installerPath,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "/silent /install",
            };
            using Process? installer = Process.Start(startInfo);
            if (installer is null)
                throw new InvalidOperationException("The WebView2 installer could not be started.");
            if (!installer.WaitForExit((int)WebViewInstallTimeout.TotalMilliseconds))
            {
                try
                {
                    installer.Kill(entireProcessTree: true);
                    installer.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
                }
                catch (Exception terminationException)
                {
                    Logger.LogWarning("Unable to stop the timed-out WebView2 installer.", terminationException);
                }
                throw new TimeoutException($"The WebView2 installer did not finish within {WebViewInstallTimeout}.");
            }
            if (installer.ExitCode != 0)
                throw new InvalidOperationException($"The WebView2 installer exited with code {installer.ExitCode}.");

            return IsWebView2RuntimeAvailable();
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not install the WebView2 Runtime.", ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(installerPath);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Unable to delete the temporary WebView2 installer.", ex);
            }
        }
    }

    // Called only from OnStartup under #if !DEBUG; Debug builds compile the call
    // site out, so IDE0051 reports this Release-only crash handler as unused
    // (false positive under Debug analysis). Do not "fix" by deleting it.
#pragma warning disable IDE0051
    private void SetupExceptionHandling()
    {
#pragma warning disable IDE0053 // Use expression body for lambda expression
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogUnhandledException((Exception)e.ExceptionObject, "AppDomain.CurrentDomain.UnhandledException");
        };
#pragma warning restore IDE0053 // Use expression body for lambda expression

        Application.Current.DispatcherUnhandledException += (s, e) =>
        {
            LogUnhandledException(e.Exception, "Application.Current.DispatcherUnhandledException");
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogUnhandledException(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };
    }
#pragma warning restore IDE0051

    private void LogUnhandledException(Exception exception, string source)
    {
        // Use the indexer instead of Add so a second handler receiving the same exception
        // instance does not throw ArgumentException for a duplicate "Source" key.
        exception.Data["Source"] = source;
        Logger.LogError(exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Final counter block: a session's cumulative churn is the part that
            // explains sustained GPU/CPU cost, and it is only complete at exit.
            try
            {
                Diagnostics.PerfTraceStore.LogCounters("session end");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to log performance counters during exit.", ex);
            }

            // Isolate each disposal so one failure cannot skip WebView/service cleanup.
            try
            {
                ApplicationCompositionRoot.DisposeCurrent();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to dispose RatScannerMain during exit.", ex);
            }

            try
            {
                BlazorUI.DisposeInstance();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to dispose BlazorUI during exit.", ex);
            }
        }
        finally
        {
            SingleInstance.Cleanup();
            Logger.Flush();
            base.OnExit(e);
        }
    }
}
