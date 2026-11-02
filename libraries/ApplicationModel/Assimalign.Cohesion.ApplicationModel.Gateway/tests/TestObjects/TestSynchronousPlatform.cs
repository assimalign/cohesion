using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A platform that disposes only synchronously.
/// </summary>
internal sealed class TestSynchronousPlatform : TestPlatformBase, IDisposable
{
    public void Dispose()
    {
        RecordDisposal();
    }
}
