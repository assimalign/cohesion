using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;

namespace Assimalign.Cohesion.Database.Studio.WinUI;

/// <summary>
/// Owns the process entry point (the XAML-generated Main is disabled with DISABLE_XAML_GENERATED_MAIN)
/// so <c>--smoke [dataRoot]</c> runs headless before any WinUI window exists.
/// </summary>
public static class Program
{
    private const int attachParentProcess = -1;

    /// <summary>The process entry point.</summary>
    /// <param name="args">Command-line arguments; <c>--smoke [dataRoot]</c> selects the headless smoke run.</param>
    /// <returns>The process exit code: non-zero when a smoke step failed.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        int smoke = Array.FindIndex(args, arg => string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase));
        if (smoke >= 0)
        {
            string? dataRoot = smoke + 1 < args.Length ? args[smoke + 1] : null;
            return RunSmoke(dataRoot);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    private static int RunSmoke(string? dataRoot)
    {
        // WinExe has no console of its own: attach to the launching console when there is one
        // (redirected stdout, e.g. a pipe, keeps working without it) and always mirror to a log file.
        AttachConsole(attachParentProcess);
        string logPath = Path.Combine(AppContext.BaseDirectory, "studio-smoke.log");
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        object sync = new();

        void Write(string line)
        {
            lock (sync)
            {
                log.WriteLine(line);
                try
                {
                    Console.Out.WriteLine(line);
                    Console.Out.Flush();
                }
                catch (IOException)
                {
                    // No usable stdout; the log file still has the line.
                }
            }
        }

        int exitCode = Task.Run(() => SmokeRunner.RunAsync(dataRoot, Write)).GetAwaiter().GetResult();
        Write($"log: {logPath}");
        Write($"exit code: {exitCode}");
        return exitCode;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
