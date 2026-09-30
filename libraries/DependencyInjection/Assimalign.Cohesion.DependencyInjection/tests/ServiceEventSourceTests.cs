using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection.Internal;

using static Assimalign.Cohesion.DependencyInjection.Tests.EventSourceFixtures;

namespace Assimalign.Cohesion.DependencyInjection.Tests;

/// <summary>
/// Verifies the container's event source against the repository's EventSource convention.
/// </summary>
/// <remarks>
/// <c>DynamicMethodBuilt</c> is exercised by <see cref="ServiceProviderDynamicCodeTests"/>, which needs a
/// JIT runtime. <c>ExpressionTreeGenerated</c> is never written while the IL resolver is built in, and
/// <c>ServiceRealizationFailed</c> only when background compilation fails; the manifest test covers
/// their shapes.
/// </remarks>
[Collection(nameof(EventSourceCollection))]
public sealed class ServiceEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - ServiceEventSource: Should be named for its assembly")]
    public void GetName_ServiceEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(ServiceEventSource));

        // Assert
        name.ShouldBe(typeof(ServiceEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.DependencyInjection");
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - ServiceEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(ServiceEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.DependencyInjection", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [DependencyInjection] - ServiceEventSource: Should report each provider and scope event once with its declared payload")]
    public void ProviderLifecycle_BuildResolveAndDispose_ShouldReportEachEventOnce()
    {
        // Arrange
        using var recorder = new EventSourceRecorder(ServiceEventSource.Log, EventLevel.Verbose);
        using var builder = new ServiceProviderBuilder(new ServiceProviderOptions { EnableDynamicCode = false });
        builder.AddSingleton<ObservedSingleton>();
        builder.AddScoped<ObservedScopedDisposable>();
        var provider = (ServiceProvider)((IServiceProviderBuilder)builder).Build();
        int providerId = provider.GetHashCode();

        // Act
        provider.GetRequiredService<ObservedSingleton>();
        using (IServiceScope scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ObservedScopedDisposable>();
        }
        provider.Dispose();

        // Assert
        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        EventWrittenEventArgs built = Single(events, "ServiceProviderBuilt", "serviceProviderHashCode", providerId);
        built.PayloadNames.ShouldBe(["serviceProviderHashCode", "singletonServices", "scopedServices", "transientServices", "closedGenericsServices", "openGenericsServices"]);
        built.Level.ShouldBe(EventLevel.Informational);
        built.Payload.ShouldBe([providerId, 1, 1, 0, 0, 0]);

        EventWrittenEventArgs descriptors = Single(events, "ServiceProviderDescriptors", "serviceProviderHashCode", providerId);
        descriptors.PayloadNames.ShouldBe(["serviceProviderHashCode", "descriptors", "chunkIndex", "chunkCount"]);
        descriptors.Level.ShouldBe(EventLevel.Verbose);
        descriptors.Payload![1].ShouldBeOfType<string>().ShouldContain(typeof(ObservedSingleton).ToString(), Case.Sensitive);

        foreach (Type serviceType in new[] { typeof(ObservedSingleton), typeof(ObservedScopedDisposable) })
        {
            EventWrittenEventArgs callSite = events.Where(e => e.EventName == "CallSiteBuilt" && Equals(e.Payload![0], serviceType.ToString())).ShouldHaveSingleItem();
            callSite.PayloadNames.ShouldBe(["serviceType", "callSite", "chunkIndex", "chunkCount", "serviceProviderHashCode"]);
            callSite.Payload![4].ShouldBe(providerId);

            EventWrittenEventArgs resolved = events.Where(e => e.EventName == "ServiceResolved" && Equals(e.Payload![0], serviceType.ToString())).ShouldHaveSingleItem();
            resolved.PayloadNames.ShouldBe(["serviceType", "serviceProviderHashCode"]);
            resolved.Payload![1].ShouldBe(providerId);
        }

        // One for the scope, then one for the root scope when the provider is disposed.
        EventWrittenEventArgs[] disposed = events.Where(e => e.EventName == "ScopeDisposed" && Equals(e.Payload![0], providerId)).ToArray();
        disposed.Length.ShouldBe(2);
        disposed[0].PayloadNames.ShouldBe(["serviceProviderHashCode", "scopedServicesResolved", "disposableServices"]);
        disposed[0].Payload.ShouldBe([providerId, 1, 1]);
    }

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

    private static EventWrittenEventArgs Single(IEnumerable<EventWrittenEventArgs> events, string eventName, string payloadName, object payloadValue) =>
        events.Where(e => e.EventName == eventName && e.PayloadNames!.Contains(payloadName) && Equals(e.Payload![e.PayloadNames.IndexOf(payloadName)], payloadValue))
            .ShouldHaveSingleItem();
}
