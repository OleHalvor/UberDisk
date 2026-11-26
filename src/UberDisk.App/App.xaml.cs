using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace UberDisk.App;

public partial class App : Application
{
    public const string RepoUrl = "https://github.com/OleHalvor/UberDisk";

    /// <summary>The product version, such as 0.2.0, without the build metadata the SDK appends.</summary>
    public static string Version { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UberDisk", "crash.log");

    private static int _reporting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // UI thread: report and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception, fatal: false);
            args.Handled = true;
        };
        // Scan and duplicate threads: .NET ends the process after this, so log and tell the user first.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Report(ex, fatal: args.IsTerminating);
        };
        // Faulted tasks nobody awaited: log only, they don't stop the app.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception, "unobserved task");
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private static string Details(Exception ex, string kind) =>
        $"UberDisk {Version} ({kind})\r\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
        $"{RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}\r\n\r\n{ex}\r\n";

    /// <summary>Appends to %LOCALAPPDATA%\UberDisk\crash.log, keeping the file small.</summary>
    private static void Log(Exception ex, string kind)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            if (File.Exists(CrashLog) && new FileInfo(CrashLog).Length > 256 * 1024) File.Delete(CrashLog);
            File.AppendAllText(CrashLog, Details(ex, kind) + new string('-', 60) + "\r\n", Encoding.UTF8);
        }
        catch { /* nowhere left to report to */ }
    }

    private static void Report(Exception ex, bool fatal)
    {
        string kind = fatal ? "crash" : "error";
        Log(ex, kind);
        if (Demo.ShotsDir != null) return; // unattended
        if (Interlocked.Exchange(ref _reporting, 1) == 1) return; // one dialog at a time

        void Show()
        {
            try
            {
                string text = (fatal ? "UberDisk hit an error it can't recover from and has to close." : "UberDisk hit an error.") +
                              $"\n\n{ex.GetType().Name}: {ex.Message}\n\nThe details are saved in {CrashLog}." +
                              "\n\nCopy the details and open GitHub to report it?";
                var answer = MessageBox.Show(text, "UberDisk hit an error", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
                if (answer == MessageBoxResult.Yes)
                {
                    try { Clipboard.SetText(Details(ex, kind)); } catch { /* clipboard busy */ }
                    OpenUrl(RepoUrl + "/issues/new");
                }
            }
            finally { Interlocked.Exchange(ref _reporting, 0); }
        }

        var dispatcher = Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess() || dispatcher.HasShutdownStarted) Show();
        else
        {
            // From a worker thread: show it on the UI thread and wait, so the process doesn't end under the dialog.
            try { dispatcher.Invoke(Show, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromMinutes(10)); }
            catch { Show(); }
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser */ }
    }
}
