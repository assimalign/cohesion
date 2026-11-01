using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A platform that disposes both ways, so a test can tell which one the gateway chose.
/// </summary>
internal sealed class TestPlatform : TestPlatformBase, IAsyncDisposable, IDisposable
{
    private int _synchronousDisposals;

    /// <summary>How many times <see cref="Dispose"/> ran.</summary>
    public int SynchronousDisposals => Volatile.Read(ref _synchronousDisposals);

    public async ValueTask DisposeAsync()
    {
        // Completes later, so a caller that does not await the disposal is caught out.
        await Task.Yield();
        RecordDisposal();
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _synchronousDisposals);
        RecordDisposal();
    }
}
