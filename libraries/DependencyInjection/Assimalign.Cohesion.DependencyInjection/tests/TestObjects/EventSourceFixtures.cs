using System;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Services resolved while the container's event source is observed.
/// </summary>
internal static class EventSourceFixtures
{
    internal sealed class ObservedSingleton
    {
    }

    internal sealed class ObservedScopedDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
