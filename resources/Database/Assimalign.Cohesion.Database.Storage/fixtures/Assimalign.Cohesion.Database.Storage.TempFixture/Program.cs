using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage.TempFixture;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length > 1 && args[1] == "lease-probe")
        {
            Directory.CreateDirectory(args[0]);
            File.WriteAllBytes(Path.Combine(args[0], "cohesion-00000000000000000000000000000001.ctmp"), [1]);
            try
            {
                using var lease = StorageTempSpace.Create(args[0]);
                Console.WriteLine("LEASE_OK");
            }
            catch (IOException error) when (!OperatingSystem.IsWindows())
            { Console.WriteLine("LEASE_REFUSED"); Console.WriteLine(error.Message); }
            return;
        }
        using var space = StorageTempSpace.Create(args[0], 16 * 1024 * 1024);
        using var statement = space.CreateStatement(16 * 1024 * 1024);
        using var run = statement.CreateRun();
        run.Append(new byte[32768]); run.Seal();
        // Also model a platform that did not unlink its delete-on-close file after a crash.
        // The parent kills this real process while both the lease and actual run remain open.
        File.WriteAllBytes(Path.Combine(args[0], "cohesion-" + Guid.NewGuid().ToString("N") + ".ctmp"), new byte[8192]);
        Console.WriteLine("READY"); Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
