using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections.AotSample;

/// <summary>
/// The NativeAOT guard for the AMQP engine: a broker and a client over TCP loopback in one process, and one
/// check per feature, so a published binary that lost a code path to trimming or AOT compilation fails.
/// </summary>
internal static class Program
{
    // The outer hang guard for the whole run; it is not a timing measurement.
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    public static async Task<int> Main()
    {
        using CancellationTokenSource timeout = new(_timeout);
        AmqpAotLoopback loopback;

        try
        {
            loopback = await AmqpAotLoopback.StartAsync(timeout.Token);
        }
        // The guard reports a failed establishment as a check rather than as a crash, whatever its cause.
        catch (Exception exception)
        {
            Console.WriteLine($"FAIL establishment: {exception.GetType().FullName}: {exception.Message}");
            Console.WriteLine($"Amqp.Connections NativeAOT: 0/{AmqpAotChecks.All.Count} checks passed.");

            return 1;
        }

        int passed = 0;

        await using (loopback)
        {
            foreach (AmqpAotCheck check in AmqpAotChecks.All)
            {
                try
                {
                    await check.RunAsync(loopback, timeout.Token);
                    passed++;
                    Console.WriteLine($"PASS {check.Name}");
                }
                // The guard reports every check: whatever a check throws is that check's failure, and the next
                // check still runs.
                catch (Exception exception)
                {
                    Console.WriteLine($"FAIL {check.Name}: {exception.GetType().FullName}: {exception.Message}");
                }
            }
        }

        Console.WriteLine($"Amqp.Connections NativeAOT: {passed}/{AmqpAotChecks.All.Count} checks passed.");

        return passed == AmqpAotChecks.All.Count ? 0 : 1;
    }
}
