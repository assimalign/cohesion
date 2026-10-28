using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// Waits for a condition a background loop makes true, such as a lease renewal.
/// </summary>
internal static class Eventually
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        long start = Stopwatch.GetTimestamp();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        while (!condition.Invoke())
        {
            if (Stopwatch.GetElapsedTime(start) > limit)
            {
                throw new TimeoutException($"The condition was not met within {limit}.");
            }

            await Task.Delay(20);
        }
    }
}
