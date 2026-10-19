using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Appends unhandled exceptions to <c>studio-crash.log</c> next to the executable.</summary>
internal static class CrashLog
{
    private static readonly object _sync = new();

    public static string PathName { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "studio-crash.log");

    public static void Write(string source, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                File.AppendAllText(PathName, $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never throw from an exception handler.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
