using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDatabaseEventSourceTests
{
    private static readonly string[] _provisioningStartPayload = ["engineName", "database", "schema", "schemaHash", "mode", "stepCount"];
    private static readonly string[] _provisioningStopPayload = ["engineName", "database", "schema", "schemaHash", "stepCount", "outcome", "durationMilliseconds"];
    private static readonly string[] _deferredPayload = ["engineName", "database", "table", "objectName", "kind", "position", "stepCount", "rowsProcessed", "totalRows"];

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: real apply, skip and verify each close one activity")]
    [InlineData("Applied")]
    [InlineData("Skipped")]
    [InlineData("Verified")]
    public async Task Provisioning_InlineOutcome_ShouldPairOnce(string outcome)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", timeout.Token);
        var schema = TraceSchema(false).Compile();
        if (outcome != "Applied")
        {
            await database.ApplySchemaAsync(schema, timeout.Token);
        }
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);

        // Act
        if (outcome == "Verified")
        {
            await database.SchemaProvisioner.VerifyAsync(schema, timeout.Token);
        }
        else
        {
            await database.ApplySchemaAsync(schema, timeout.Token);
        }

        // Assert
        var start = NewEvent(recorder, 13, "ProvisioningStart", _provisioningStartPayload);
        var stop = NewEvent(recorder, 14, "ProvisioningStop", _provisioningStopPayload);
        start.Level.ShouldBe(EventLevel.Informational);
        start.Payload!.Take(4).ShouldBe([engine.Name, "traced", "traced", schema.Hash]);
        stop.Payload![4].ShouldBe(outcome == "Applied" ? 1 : 0);
        stop.Payload[5].ShouldBe(outcome);
        ((double)stop.Payload[6]!).ShouldBeGreaterThanOrEqualTo(0);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 15);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: failure and cancellation close the inline pair without user messages")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provisioning_InlineFailure_ShouldPreserveOutcomeAndPayload(bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", timeout.Token);
        var schema = TraceSchema(false).Compile();
        var failure = new InvalidOperationException("private user payload");
        database.SchemaProvisioner.StepApplied = _ =>
        {
            if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            throw failure;
        };
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);

        // Act
        if (cancel)
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await database.ApplySchemaAsync(schema, cancellation.Token));
        }
        else
        {
            var thrown = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await database.ApplySchemaAsync(schema, timeout.Token));
            thrown.InnerException.ShouldBeSameAs(failure);
        }

        // Assert
        NewEvent(recorder, 13, "ProvisioningStart", _provisioningStartPayload);
        NewEvent(recorder, 14, "ProvisioningStop", _provisioningStopPayload).Payload![5].ShouldBe(cancel ? "Cancelled" : "Error");
        if (cancel)
        {
            recorder.Events.ShouldNotContain(entry => entry.EventId == 15);
        }
        else
        {
            var failed = NewEvent(recorder, 15, "ProvisioningFailed", "engineName", "database", "schema", "position", "kind", "code", "exceptionType");
            failed.Level.ShouldBe(EventLevel.Error);
            failed.Payload.ShouldBe([engine.Name, "traced", "traced", 1, "AddTable", "COHSQLP004", typeof(InvalidOperationException).FullName]);
        }
        recorder.Events.SelectMany(entry => entry.Payload ?? []).ShouldNotContain(value => Equals(value, failure.Message));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - DeferredStep: real migration reports bounded progress and exactly one outcome")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DeferredStep_RealCheck_ShouldReportLifecycleOnce(bool fail, bool pauseBetweenCallbacks)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", timeout.Token);
        await database.ApplySchemaAsync(TraceSchema(false).Compile(), timeout.Token);
        await using (var session = await database.CreateSessionAsync(timeout.Token))
        {
            await session.ExecuteAsync("INSERT INTO items (Id) VALUES " + string.Join(",", Enumerable.Range(1, 130).Select(value => $"({value})")), cancellationToken: timeout.Token);
        }
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
        int callbacks = 0;
        database.SchemaProvisioner.DeferredProgress = (_, _) =>
        {
            if (Interlocked.Increment(ref callbacks) == 1 && pauseBetweenCallbacks)
            {
                // A scheduler pause after the first callback legitimately permits another event.
                timeout.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(1100));
                timeout.Token.ThrowIfCancellationRequested();
            }
        };
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);

        // Act
        try
        {
            await database.SchemaProvisioner.ApplyAsync(TraceSchema(true, fail).Compile(), timeout.Token, deferDataScalingSteps: true);
            await entered.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            release.Set();
        }
        while (database.SchemaProvisioner.Deferred is not null)
        {
            await Task.Delay(1, timeout.Token);
        }

        // Assert
        NewEvent(recorder, 13, "ProvisioningStart", _provisioningStartPayload);
        NewEvent(recorder, 14, "ProvisioningStop", _provisioningStopPayload).Payload![5].ShouldBe("Deferred");
        var bound = NewEvent(recorder, 18, "DeferredStepBound", _deferredPayload);
        bound.Payload!.Take(7).ShouldBe([engine.Name, "traced", "items", "ck_trace", "AddConstraint", 1, 1]);
        bound.Level.ShouldBe(EventLevel.Informational);
        callbacks.ShouldBeGreaterThanOrEqualTo(130);
        var progress = recorder.Events.Where(entry => entry.EventId == 19).ToArray();
        progress.ShouldNotBeEmpty();
        foreach (var entry in progress)
        {
            entry.EventName.ShouldBe("DeferredStepProgress");
            entry.PayloadNames.ShouldBe(_deferredPayload);
            entry.Level.ShouldBe(EventLevel.Verbose);
            entry.Payload!.Take(7).ShouldBe([engine.Name, "traced", "items", "ck_trace", "AddConstraint", 1, 1]);
            ((long)entry.Payload![7]!).ShouldBeInRange(0, 130);
            entry.Payload[8].ShouldBe(130L);
        }
        var completed = NewEvent(recorder, 20, "DeferredStepCompleted", [.. _deferredPayload, "outcome", "durationMilliseconds"]);
        completed.Payload![9].ShouldBe(fail ? "Error" : "Applied");
        completed.Payload[8].ShouldBe(130L);
        if (fail)
        {
            var failed = NewEvent(recorder, 21, "DeferredStepFailed", [.. _deferredPayload, "code", "exceptionType"]);
            failed.Level.ShouldBe(EventLevel.Error);
            failed.Payload![9].ShouldBe("COHSQLP004");
            ((string)failed.Payload[10]!).ShouldNotBeNullOrEmpty();
            database.IsOffline.ShouldBeTrue();
        }
        else
        {
            completed.Payload[7].ShouldBe(130L);
            database.Provisioning.ShouldBeNull();
            recorder.Events.ShouldNotContain(entry => entry.EventId == 21);
        }
        recorder.Events.ShouldNotContain(entry => entry.EventId == 17);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - DeferredStep: a listener attached during a real step sees remaining progress and completion")]
    [InlineData(false, "Applied")]
    [InlineData(true, "Applied")]
    [InlineData(false, "Error")]
    [InlineData(false, "Cancelled")]
    public async Task DeferredStep_ListenerAttachedDuringStep_ShouldReportRemainingLifecycle(bool attachAfterProgress, string outcome)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", timeout.Token);
        await database.ApplySchemaAsync(TraceSchema(false).Compile(), timeout.Token);
        await using (var session = await database.CreateSessionAsync(timeout.Token))
        {
            await session.ExecuteAsync("INSERT INTO items (Id) VALUES " + string.Join(",", Enumerable.Range(1, 130).Select(value => $"({value})")), cancellationToken: timeout.Token);
        }
        int callbacks = 0;
        database.SchemaProvisioner.DeferredProgress = (_, _) => Interlocked.Increment(ref callbacks);
        if (attachAfterProgress)
        {
            database.SchemaProvisioner.StepApplied = _ => { entered.TrySetResult(); release.Wait(timeout.Token); };
        }
        else
        {
            database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); release.Wait(token); };
        }
        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        EventSourceRecorder? recorder = null;

        // Act
        try
        {
            try
            {
                await database.SchemaProvisioner.ApplyAsync(TraceSchema(true, outcome == "Error").Compile(), timeout.Token, deferDataScalingSteps: true);
                await entered.Task.WaitAsync(timeout.Token);
                recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
                if (outcome == "Cancelled")
                {
                    await database.SchemaProvisioner.StopDeferredAsync();
                }
            }
            finally
            {
                release.Set();
            }
            while (database.SchemaProvisioner.Deferred is not null)
            {
                await Task.Delay(1, timeout.Token);
            }

            // Assert
            if (outcome == "Cancelled")
            {
                callbacks.ShouldBe(0);
                database.Provisioning.ShouldNotBeNull();
            }
            else
            {
                callbacks.ShouldBeGreaterThanOrEqualTo(130);
            }
            database.IsOffline.ShouldBe(outcome == "Error");
            recorder.ShouldNotBeNull();
            recorder.Events.ShouldNotContain(entry => entry.EventId == 13 || entry.EventId == 14 || entry.EventId == 18);
            if (!attachAfterProgress && outcome != "Cancelled")
            {
                var progress = recorder.Events.Where(entry => entry.EventId == 19).ToArray();
                progress.ShouldNotBeEmpty();
                foreach (var entry in progress)
                {
                    entry.EventName.ShouldBe("DeferredStepProgress");
                    entry.PayloadNames.ShouldBe(_deferredPayload);
                    entry.Payload!.Take(7).ShouldBe([engine.Name, "traced", "items", "ck_trace", "AddConstraint", 1, 1]);
                    entry.Payload![8].ShouldBe(130L);
                }
            }
            var completed = NewEvent(recorder, 20, "DeferredStepCompleted", [.. _deferredPayload, "outcome", "durationMilliseconds"]);
            completed.Payload!.Take(7).ShouldBe([engine.Name, "traced", "items", "ck_trace", "AddConstraint", 1, 1]);
            completed.Payload![9].ShouldBe(outcome);
            completed.Payload[10].ShouldBe(0d);
            if (outcome == "Applied")
            {
                database.Provisioning.ShouldBeNull();
                completed.Payload[7].ShouldBe(130L);
                completed.Payload[8].ShouldBe(130L);
            }
            else
            {
                var progress = database.Provisioning.ShouldNotBeNull();
                completed.Payload[7].ShouldBe(progress.RowsProcessed ?? -1);
                completed.Payload[8].ShouldBe(progress.TotalRows ?? -1);
            }
            if (outcome == "Error")
            {
                var failed = NewEvent(recorder, 21, "DeferredStepFailed", [.. _deferredPayload, "code", "exceptionType"]);
                failed.Payload!.Take(9).ShouldBe(completed.Payload.Take(9));
                failed.Payload![9].ShouldBe("COHSQLP004");
            }
            else
            {
                recorder.Events.ShouldNotContain(entry => entry.EventId == 21);
            }
        }
        finally
        {
            recorder?.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - DeferredStep: cancellation closes its lifetime without a failure")]
    public async Task DeferredStep_Cancelled_ShouldCompleteWithoutFailure()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", timeout.Token);
        await database.ApplySchemaAsync(TraceSchema(false).Compile(), timeout.Token);
        await using (var session = await database.CreateSessionAsync(timeout.Token))
        {
            await session.ExecuteAsync("INSERT INTO items (Id) VALUES (1)", cancellationToken: timeout.Token);
        }
        database.SchemaProvisioner.BeforeDeferredStep = (_, token) => { entered.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); };
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);

        // Act
        await database.SchemaProvisioner.ApplyAsync(TraceSchema(true).Compile(), timeout.Token, deferDataScalingSteps: true);
        await entered.Task.WaitAsync(timeout.Token);
        await database.SchemaProvisioner.StopDeferredAsync();

        // Assert
        NewEvent(recorder, 18, "DeferredStepBound", _deferredPayload);
        NewEvent(recorder, 20, "DeferredStepCompleted", [.. _deferredPayload, "outcome", "durationMilliseconds"]).Payload![9].ShouldBe("Cancelled");
        recorder.Events.ShouldNotContain(entry => entry.EventId == 21);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning tracing: disabled typed writes allocate and time nothing")]
    public async Task ProvisioningWrites_Disabled_ShouldAllocateNothing()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", CancellationToken.None);
        var trace = new SqlProvisioningTrace(TraceSchema(false).Compile()) { Failure = new InvalidOperationException("private") };
        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        long allocated = long.MaxValue;
        Write();

        // Act
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10000; index++) { Write(); }
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert
        allocated.ShouldBe(0);
        SqlDatabaseEventSource.Log.ProvisioningStart(database, trace.Schema, "Apply").ShouldBe(0);
        SqlDatabaseEventSource.Log.DeferredStepBound(database, trace).ShouldBe(0);

        void Write()
        {
            SqlDatabaseEventSource.Log.ProvisioningStart(database, trace.Schema, "Apply");
            SqlDatabaseEventSource.Log.ProvisioningStop(database, trace);
            SqlDatabaseEventSource.Log.ProvisioningFailed(database, trace);
            SqlDatabaseEventSource.Log.DeferredStepBound(database, trace);
            SqlDatabaseEventSource.Log.DeferredStepProgress(database, trace);
            SqlDatabaseEventSource.Log.DeferredStepCompleted(database, trace);
            SqlDatabaseEventSource.Log.DeferredStepFailed(database, trace, trace.Failure!);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - DeferredStep progress: real row callbacks publish at most once per second")]
    public async Task DeferredStep_ManyRowCallbacks_ShouldThrottleProgress()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", timeout.Token);
        await database.ApplySchemaAsync(TraceSchema(false).Compile(), timeout.Token);
        await using (var session = await database.CreateSessionAsync(timeout.Token))
        {
            await session.ExecuteAsync("INSERT INTO items (Id) VALUES " + string.Join(",", Enumerable.Range(1, 130).Select(value => $"({value})")), cancellationToken: timeout.Token);
        }
        int callbacks = 0;
        database.SchemaProvisioner.DeferredProgress = (_, _) => { Interlocked.Increment(ref callbacks); Thread.Sleep(12); };
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        var elapsed = Stopwatch.StartNew();

        // Act
        await database.SchemaProvisioner.ApplyAsync(TraceSchema(true).Compile(), timeout.Token, deferDataScalingSteps: true);
        while (database.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, timeout.Token); }

        // Assert
        callbacks.ShouldBeGreaterThanOrEqualTo(130);
        elapsed.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(1));
        var progress = recorder.Events.Where(entry => entry.EventId == 19).ToArray();
        progress.Length.ShouldBeGreaterThanOrEqualTo(2);
        progress.Length.ShouldBeLessThanOrEqualTo((int)elapsed.Elapsed.TotalSeconds + 1);
        progress.Length.ShouldBeLessThan(callbacks / 10);
        for (int index = 1; index < progress.Length; index++)
        {
            (progress[index].TimeStamp - progress[index - 1].TimeStamp).ShouldBeGreaterThan(TimeSpan.FromMilliseconds(950));
        }
        NewEvent(recorder, 20, "DeferredStepCompleted", [.. _deferredPayload, "outcome", "durationMilliseconds"]).Payload![7].ShouldBe(130L);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Provisioning: a late error listener sees the step failure without an unmatched stop")]
    public async Task Provisioning_ListenerAttachedDuringStep_ShouldReportFailureOnly()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(UniqueEngineName(), new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("traced", CancellationToken.None);
        EventSourceRecorder? recorder = null;
        database.SchemaProvisioner.StepApplied = _ =>
        {
            recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);
            throw new InvalidOperationException("private user value");
        };

        // Act
        try
        {
            await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await database.ApplySchemaAsync(TraceSchema(false).Compile(), CancellationToken.None));

            // Assert
            var failed = NewEvent(recorder.ShouldNotBeNull(), 15, "ProvisioningFailed", "engineName", "database", "schema", "position", "kind", "code", "exceptionType");
            failed.Payload!.Skip(3).ShouldBe([1, "AddTable", "COHSQLP004", typeof(InvalidOperationException).FullName]);
            recorder.Events.ShouldNotContain(entry => entry.EventId == 13 || entry.EventId == 14);
        }
        finally
        {
            recorder?.Dispose();
        }
    }

    private static SqlSchema TraceSchema(bool check, bool fail = false) => SqlSchema.Create("traced", schema => schema.Table<TraceItem>("items", table =>
    {
        table.Column(row => row.Id);
        if (check) { table.Check("ck_trace", fail ? "Id < 130" : "Id > 0"); }
    }));

    private sealed record TraceItem(int Id);
}
