using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections.Tests;

[Collection(nameof(EventSourceCollection))]
public class ConnectionLayerEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections] - ConnectionLayerEventSource: Should be named for its assembly")]
    public void GetName_ConnectionLayerEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(ConnectionLayerEventSource));

        // Assert
        name.ShouldBe(typeof(ConnectionLayerEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Connections");
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - ConnectionLayerEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(ConnectionLayerEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Connections", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - ConnectionLayerEventSource: Should report a failed upgrade once and restore the gauge")]
    public async Task UpgradeFailed_OnRejectedConnection_ShouldReportOnceAndRestoreTheGauge()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using EventSourceRecorder recorder = new(ConnectionLayerEventSource.Log, EventLevel.Verbose);
        long currentBefore = ConnectionLayerEventSource.Log.CurrentUpgrades;
        long failedBefore = ConnectionLayerEventSource.Log.FailedUpgrades;
        TestConnection rejected = new();
        TestConnection ready = new();
        ControlledConnectionLayer layer = new();
        layer.Fail(rejected, new IOException("Not a handshake."));
        BlockingConnectionListener inner = new();
        inner.Enqueue(rejected);
        inner.Enqueue(ready);
        IConnectionListener listener = inner.Use(layer);

        // Act — disposal waits for every upgrade, so the failure has been reported once it returns.
        IConnection accepted = await listener.AcceptAsync(timeout.Token);
        await listener.DisposeAsync();

        // Assert
        accepted.ShouldBeSameAs(ready);
        ConnectionLayerEventSource.Log.CurrentUpgrades.ShouldBe(currentBefore);
        ConnectionLayerEventSource.Log.FailedUpgrades.ShouldBe(failedBefore + 1);

        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        EventWrittenEventArgs failed = events.ShouldHaveSingleItem();
        failed.EventName.ShouldBe("UpgradeFailed");
        failed.Level.ShouldBe(EventLevel.Warning);
        failed.PayloadNames.ShouldBe(["connectionId", "remoteEndPoint", "exceptionType", "exceptionMessage"]);
        failed.Payload![0].ShouldBe(rejected.Id.ToString());
        failed.Payload[1].ShouldBe(rejected.RemoteEndPoint!.ToString());
        failed.Payload[2].ShouldBe(typeof(IOException).FullName);
        failed.Payload[3].ShouldBe("Not a handshake.");
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - ConnectionLayerEventSource: Should not report upgrades that disposal cancels")]
    public async Task UpgradeFailed_OnUpgradeCanceledByDisposal_ShouldNotReport()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using EventSourceRecorder recorder = new(ConnectionLayerEventSource.Log, EventLevel.Verbose);
        long currentBefore = ConnectionLayerEventSource.Log.CurrentUpgrades;
        long failedBefore = ConnectionLayerEventSource.Log.FailedUpgrades;
        TestConnection stalled = new();
        ControlledConnectionLayer layer = new();
        layer.Hold(stalled);
        BlockingConnectionListener inner = new();
        inner.Enqueue(stalled);
        IConnectionListener listener = inner.Use(layer);
        Task<IConnection> pending = listener.AcceptAsync(timeout.Token).AsTask();
        await layer.WhenStarted(stalled).WaitAsync(timeout.Token);

        // Act
        await listener.DisposeAsync();

        // Assert
        await Should.ThrowAsync<ObjectDisposedException>(() => pending);
        stalled.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        ConnectionLayerEventSource.Log.CurrentUpgrades.ShouldBe(currentBefore);
        ConnectionLayerEventSource.Log.FailedUpgrades.ShouldBe(failedBefore);
        recorder.Events.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - ConnectionLayerEventSource: Should publish its upgrade counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishUpgradeCounters()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        string[] counters = ["current-upgrades", "failed-upgrades"];

        // Act
        using EventSourceRecorder recorder = new(ConnectionLayerEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, timeout.Token);
    }
}
