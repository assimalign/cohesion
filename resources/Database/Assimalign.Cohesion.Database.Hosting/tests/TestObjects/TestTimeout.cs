using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Bounded cancellation tokens so a hung exchange fails the test instead of the
/// run (xUnit v2 has no ambient test cancellation).
/// </summary>
internal static class TestTimeout
{
    public static CancellationToken Token(int seconds = 10)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;
}
