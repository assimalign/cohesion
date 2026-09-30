using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection.Internal;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies the allocation behavior of the scope's disposable tracking.
/// </summary>
public sealed class ServiceProviderEngineScopeTests
{
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - CaptureDisposable: Should not allocate for a service that is not disposable")]
    public void CaptureDisposable_ForNonDisposableService_ShouldNotAllocate()
    {
        // Arrange: every service activation passes through CaptureDisposable, and most services are
        // not disposable.
        using var builder = new ServiceProviderBuilder();
        using var provider = (ServiceProvider)((IServiceProviderBuilder)builder).Build();
        using var scope = (ServiceProviderEngineScope)provider.CreateScope();
        object service = new object();
        scope.CaptureDisposable(service);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            scope.CaptureDisposable(service);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0L);
    }
}
