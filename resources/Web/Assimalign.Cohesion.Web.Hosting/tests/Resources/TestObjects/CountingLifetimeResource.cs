using System;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal sealed class CountingLifetimeResource : IDisposable
{
    public int DisposeCount { get; private set; }

    public void Dispose() => DisposeCount++;
}
