using System;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection.Internal;

using static Assimalign.Cohesion.DependencyInjection.Tests.EventSourceFixtures;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies the container's event source.
/// </summary>
[Collection(nameof(EventSourceCollection))]
public sealed class ServiceEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - ServiceEventSource: Should stop tracking providers that were never disposed once they are collected")]
    public void ServiceProviderBuilt_ForProvidersNeverDisposed_ShouldPruneCollectedProviders()
    {
        // Arrange: the event source remembers every provider for listeners that attach later, and one
        // that is never disposed used to stay in that list for the life of the process.
        const int providerCount = 2_000;
        BuildAndDropProviders(providerCount);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Act
        using var builder = new ServiceProviderBuilder();
        using var provider = (ServiceProvider)((IServiceProviderBuilder)builder).Build();

        // Assert: pruning runs whenever the list has doubled since the last pruning, so it stays within
        // twice the providers alive at that point; a collection every 50 builds keeps that small.
        ServiceEventSource.Log.TrackedProviderCount.ShouldBeLessThan(200);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BuildAndDropProviders(int count)
    {
        for (int i = 0; i < count; i++)
        {
            using var builder = new ServiceProviderBuilder();
            builder.AddSingleton<ObservedSingleton>();
            ((IServiceProviderBuilder)builder).Build();

            if (i % 50 == 0)
            {
                GC.Collect();
            }
        }
    }
}
