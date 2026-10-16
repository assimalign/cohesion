using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Exe.TestHost;

// Built from source for lifecycle tests. No external binaries, shell, network or unbounded waits.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = watchdog.Token;
        switch (args[0])
        {
            case "echo":
                for (var i = 1; i < args.Length; i++)
                {
                    await Console.Out.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i])).AsMemory(), token);
                }
                return 0;
            case "write":
                await Task.WhenAll(
                    WriteBytesAsync(Console.OpenStandardOutput(), int.Parse(args[1], CultureInfo.InvariantCulture), (byte)'o', token),
                    WriteBytesAsync(Console.OpenStandardError(), int.Parse(args[2], CultureInfo.InvariantCulture), (byte)'e', token));
                return 0;
            case "stdin":
                await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput(), token);
                return 0;
            case "sleep":
                await Console.Out.WriteLineAsync(Environment.ProcessId.ToString(CultureInfo.InvariantCulture).AsMemory(), token);
                await Console.Out.FlushAsync(token);
                await Task.Delay(int.Parse(args[1], CultureInfo.InvariantCulture), token);
                return 0;
            case "exit":
                return int.Parse(args[1], CultureInfo.InvariantCulture);
            case "context":
                await Console.Out.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(Environment.CurrentDirectory)).AsMemory(), token);
                for (var i = 1; i < args.Length; i++)
                {
                    var value = Environment.GetEnvironmentVariable(args[i]) ?? "<unset>";
                    await Console.Out.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).AsMemory(), token);
                }
                return 0;
            case "tree":
            case "child":
                var directory = args[1];
                await WritePidAsync(Path.Combine(directory, args[0] + ".pid"), token);
                var launch = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!, UseShellExecute = false, CreateNoWindow = true
                };
                launch.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Content.Exe.TestHost.dll"));
                launch.ArgumentList.Add(args[0] == "tree" ? "child" : "grandchild");
                launch.ArgumentList.Add(directory);
                using (var child = Process.Start(launch)!)
                {
                    try
                    {
                        await child.WaitForExitAsync(token);
                    }
                    finally
                    {
                        if (!child.HasExited)
                        {
                            child.Kill(entireProcessTree: true);
                            await child.WaitForExitAsync(CancellationToken.None);
                        }
                    }
                }
                return 0;
            case "grandchild":
                await WritePidAsync(Path.Combine(args[1], "grandchild.pid"), token);
                await Task.Delay(TimeSpan.FromSeconds(12), token);
                return 0;
            default:
                return 64;
        }
    }

    private static async Task WritePidAsync(string path, CancellationToken cancellationToken = default)
    {
        // Publish only after closing the writer; Windows readers must never race its sharing mode.
        await File.WriteAllTextAsync(path + ".tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        File.Move(path + ".tmp", path);
    }

    private static async Task WriteBytesAsync(Stream stream, int count, byte value, CancellationToken cancellationToken = default)
    {
        var bytes = new byte[8192];
        Array.Fill(bytes, value);
        while (count > 0)
        {
            var written = Math.Min(count, bytes.Length);
            await stream.WriteAsync(bytes.AsMemory(0, written), cancellationToken);
            count -= written;
        }
        await stream.FlushAsync(cancellationToken);
    }
}
