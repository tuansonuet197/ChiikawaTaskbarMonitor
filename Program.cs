using System.Diagnostics;
using UsagiBatteryMonitor.Services;
using UsagiBatteryMonitor.UI;

namespace UsagiBatteryMonitor;

internal static class Program
{
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsagiBatteryMonitor", "crash.log");

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("UnobservedTask", e.Exception); e.SetObserved(); };

        if (args.Contains("--diag"))
        {
            RunDiagnostics();
            return;
        }

        using var singleInstance = new Mutex(true, @"Local\UsagiBatteryMonitor_SingleInstance", out bool createdNew);
        if (!createdNew) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext());
    }

    private static void RunDiagnostics()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var ctx = new TrayApplicationContext();
        var timer = new System.Windows.Forms.Timer { Interval = 4500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var proc = Process.GetCurrentProcess();
            var settings = SettingsStore.Load();
            var agg = new BatteryAggregator();
            var devices = agg.RefreshAsync(settings).GetAwaiter().GetResult();
            string outPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsagiBatteryMonitor", "diag.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var lines = new List<string>
            {
                $"RAM_MB={proc.WorkingSet64 / (1024.0 * 1024.0):F1}",
                $"Private_MB={proc.PrivateMemorySize64 / (1024.0 * 1024.0):F1}",
                $"CPU_Sec={proc.TotalProcessorTime.TotalSeconds:F3}",
                $"StartupEnabled={StartupManager.IsEnabled()}",
                $"DevicesCount={devices.Count}"
            };
            foreach (var d in devices)
                lines.Add($"  - {d.Name} [{d.CategoryLabel} | {d.Transport}]: {d.BatteryPercent}% (Charging={d.IsCharging}, Voltage={d.VoltageMv}mV)");
            File.WriteAllLines(outPath, lines);
            Application.ExitThread();
        };
        timer.Start();
        Application.Run(ctx);
    }

    private static void LogCrash(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, $"[{DateTime.Now:O}] {kind}: {ex}\n\n");
        }
        catch { }
    }
}
