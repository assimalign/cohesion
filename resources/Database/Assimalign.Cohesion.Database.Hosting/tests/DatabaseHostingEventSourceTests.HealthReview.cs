using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed partial class DatabaseHostingEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: an older concurrent check cannot overwrite a newer observation")]
    public async Task CheckAsync_OlderObservationCompletesLast_ShouldPreserveLatestTransition()
    {
        // Arrange: pause after the first engine's facts were read, while another check restores it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HealthFailureWorker? worker = null;
        var builder = SqlDatabaseEngine.CreateBuilder("overlapping-health");
        builder.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.Options.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new HealthFailureWorker());
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync("a", timeout.Token);
        await using var pausing = new RecordingEngine("pausing");
        var appBuilder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        appBuilder.AddEngine(engine);
        appBuilder.AddEngine(pausing);
        await using var app = appBuilder.Build();
        worker.ShouldNotBeNull().Fail("a");
        while (engine.OfflineDatabases.Count == 0) { await Task.Delay(1, timeout.Token); }
        using var recorder = new HostingEventRecorder(System.Diagnostics.Tracing.EventLevel.Informational);
        (await app.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Unhealthy);
        int inventoryReads = 0;
        pausing.Inventory = PauseAsync;

        // Act
        var older = app.Context.CheckAsync(timeout.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            await Should.ThrowAsync<OperationCanceledException>(() => app.Context.CheckAsync(new CancellationToken(true)).AsTask().WaitAsync(timeout.Token));
            await engine.OpenDatabaseAsync("a", timeout.Token);
            (await app.Context.CheckAsync(timeout.Token).AsTask().WaitAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy);
        }
        finally { release.TrySetResult(); }
        (await older.WaitAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Unhealthy);

        // Assert
        inventoryReads.ShouldBe(2);
        recorder.Events.Where(entry => entry.EventId is 16 or 17).Select(entry => entry.EventName)
            .ShouldBe(["HealthDegraded", "HealthRestored"]);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);

        async IAsyncEnumerable<DatabaseInstance> PauseAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref inventoryReads) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            yield break;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: disposal after the result must preserve that result")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_DisposedDuringTraceInventory_ShouldPreserveCompletedResult(bool tracing)
    {
        // Arrange: the tracing-only read disposes the engine before its next inventory call.
        await using var engine = new RecordingEngine();
        engine.OnOfflineRead = read =>
        {
            if (read == 2) { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        };
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var app = builder.Build();
        using var recorder = tracing ? new HostingEventRecorder(System.Diagnostics.Tracing.EventLevel.Informational) : null;

        // Act
        var result = await app.Context.CheckAsync(CancellationToken.None);

        // Assert
        result.Status.ShouldBe(HealthStatus.Healthy);
        engine.OfflineReads.ShouldBe(1);
        recorder?.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: empty applications preserve cancelled-token results")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_EmptyCancelledApplication_ShouldPreserveHealthyResult(bool tracing)
    {
        // Arrange
        await using var app = DatabaseApplication.CreateBuilder().Build();
        using var recorder = tracing ? new HostingEventRecorder(System.Diagnostics.Tracing.EventLevel.Informational) : null;

        // Act
        var result = await app.Context.CheckAsync(new CancellationToken(true));

        // Assert
        result.Status.ShouldBe(HealthStatus.Healthy);
        recorder?.Events.ShouldNotContain(entry => entry.EventId == 0 || entry.EventId == 16 || entry.EventId == 17);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: cancellation after the final core read preserves success")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_CancelledAfterFinalRead_ShouldPreserveCompletedResult(bool tracing)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = new RecordingEngine { OnOfflineRead = _ => cancellation.Cancel() };
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var app = builder.Build();
        using var recorder = tracing ? new HostingEventRecorder(System.Diagnostics.Tracing.EventLevel.Informational) : null;

        // Act
        var result = await app.Context.CheckAsync(cancellation.Token);

        // Assert
        cancellation.IsCancellationRequested.ShouldBeTrue();
        result.Status.ShouldBe(HealthStatus.Healthy);
        engine.OfflineReads.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: the core retains exception identity and cancellation type")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_CoreThrows_ShouldPreserveExceptionAndCancellation(bool tracing)
    {
        // Arrange
        var expected = new IOException("inventory probe");
        await using var engine = new RecordingEngine { InventoryException = expected };
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var app = builder.Build();
        using var recorder = tracing ? new HostingEventRecorder(System.Diagnostics.Tracing.EventLevel.Informational) : null;

        // Act
        var actual = await Should.ThrowAsync<IOException>(() => app.Context.CheckAsync(CancellationToken.None).AsTask());
        var expectedCancellation = await Should.ThrowAsync<OperationCanceledException>(() => app.Context.CheckCoreAsync(new CancellationToken(true)).AsTask());
        var cancelled = await Should.ThrowAsync<OperationCanceledException>(() => app.Context.CheckAsync(new CancellationToken(true)).AsTask());

        // Assert
        actual.ShouldBeSameAs(expected);
        cancelled.GetType().ShouldBe(expectedCancellation.GetType());
        cancelled.CancellationToken.ShouldBe(new CancellationToken(true));
        recorder?.Events.ShouldNotContain(entry => entry.EventId == 0 || entry.EventId == 16 || entry.EventId == 17);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: reentrant restoration follows the complete degradation batch")]
    public async Task CheckAsync_ReentrantRestoration_ShouldEmitInObservationOrder()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        HealthFailureWorker? worker = null;
        var builder = SqlDatabaseEngine.CreateBuilder("reentrant-health");
        builder.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.Options.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new HealthFailureWorker());
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync("a", timeout.Token);
        await engine.CreateDatabaseAsync("b", timeout.Token);
        var appBuilder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        appBuilder.AddEngine(engine);
        await using var app = appBuilder.Build();
        worker.ShouldNotBeNull().Fail("a", "b");
        while (engine.OfflineDatabases.Count != 2) { await Task.Delay(1, timeout.Token); }
        int reentries = 0;
        using var recorder = new HostingEventRecorder(System.Diagnostics.Tracing.EventLevel.Informational)
        {
            OnWritten = entry =>
            {
                if (entry.EventId == 16 && Equals(entry.Payload![2], "a"))
                {
                    engine.OpenDatabaseAsync("b", timeout.Token).AsTask().GetAwaiter().GetResult();
                    app.Context.CheckAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                    reentries++;
                }
            },
        };

        // Act
        (await app.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Unhealthy);
        await engine.OpenDatabaseAsync("a", timeout.Token);
        for (int poll = 0; poll < 3; poll++) { (await app.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Healthy); }

        // Assert
        reentries.ShouldBe(1);
        recorder.Events.Where(entry => entry.EventId is 16 or 17)
            .Select(entry => $"{entry.EventName} {entry.Payload![2]}")
            .ShouldBe(["HealthDegraded a", "HealthDegraded b", "HealthRestored b", "HealthRestored a"]);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Health tracing: replacement instances with the same condition each degrade")]
    public async Task CheckAsync_OfflineReplacement_ShouldReportNewInstance()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        HealthFailureWorker? worker = null;
        var builder = SqlDatabaseEngine.CreateBuilder("replacement-health");
        builder.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.Options.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new HealthFailureWorker());
        await using var engine = builder.Build();
        var original = await engine.CreateDatabaseAsync("same", timeout.Token);
        var appBuilder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        appBuilder.AddEngine(engine);
        await using var app = appBuilder.Build();
        using var recorder = new HostingEventRecorder(System.Diagnostics.Tracing.EventLevel.Informational);

        // Act
        worker.ShouldNotBeNull().Fail("same");
        while (engine.OfflineDatabases.Count == 0) { await Task.Delay(1, timeout.Token); }
        (await app.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Unhealthy);
        await engine.DropDatabaseAsync("same", timeout.Token);
        var replacement = await engine.CreateDatabaseAsync("same", timeout.Token);
        worker.Fail("same");
        while (engine.OfflineDatabases.Count == 0) { await Task.Delay(1, timeout.Token); }
        for (int poll = 0; poll < 3; poll++) { (await app.Context.CheckAsync(timeout.Token)).Status.ShouldBe(HealthStatus.Unhealthy); }

        // Assert
        replacement.ShouldNotBeSameAs(original);
        var degraded = recorder.Events.Where(entry => entry.EventId == 16).ToArray();
        degraded.Length.ShouldBe(2);
        foreach (var entry in degraded)
        {
            entry.PayloadNames.ShouldBe(_healthPayload);
            entry.Payload![2].ShouldBe("same");
            entry.Payload[4].ShouldBe("CheckpointFailures");
        }
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0 || entry.EventId == 17);
    }
}
