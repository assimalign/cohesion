using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlRecoveryHealthTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Health: automatic reopen does not block health or other databases' provisioning observations")]
    public async Task CheckAsync_AutomaticReopenPaused_ShouldReportPublishedStateWithoutWaiting()
    {
        // Arrange: pause the real automatic reopen before logical recovery, under the engine's
        // registry monitor. Another published database still has provisioning to report.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("health-reopen", new());
        var recovering = await engine.CreateDatabaseAsync("recovering", timeout.Token);
        var provisioning = await engine.CreateDatabaseAsync("provisioning", timeout.Token);
        provisioning.ReportProvisioning(new("CreateIndex", "items", 1, 1, true, 1, 3));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int recoveryEntries = 0;
        engine.BeforeProvisioning = database =>
        {
            if (database.Name == recovering.Name)
            {
                Interlocked.Increment(ref recoveryEntries);
                entered.TrySetResult();
                release.Wait(timeout.Token);
            }
        };
        var builder = new DatabaseApplicationBuilder(new()
        {
            ReopenInitialDelay = TimeSpan.FromMilliseconds(10),
            ReopenMaximumDelay = TimeSpan.FromMilliseconds(20),
        });
        builder.AddEngine(engine);
        await using var application = builder.Build(timeout.Token);
        Task<HealthContribution>? pendingCheck = null;
        int healthReads = 0;
        try
        {
            await ((IHost)application).StartAsync(timeout.Token);
            recovering.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "Health reopen regression.", new InvalidOperationException("Injected checkpoint failure."));
            await entered.Task.WaitAsync(timeout.Token);

            // Act: run until the minimum observation period and the health reads are exercised,
            // with every check bounded independently of the deliberately held recovery monitor.
            var clock = Stopwatch.StartNew();
            do
            {
                pendingCheck = DedicatedTestTask.Run(async () => await application.Context.CheckAsync(timeout.Token), timeout.Token);
                HealthContribution health = await pendingCheck.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);

                // Assert: the reopen service retains the offline cause while the engine's
                // published inventory continues reporting the other database's progress.
                health.Status.ShouldBe(HealthStatus.Unhealthy);
                health.Data.ShouldNotBeNull()["engine.0.offlineDatabaseCount"].ShouldBe(1);
                health.Data["engine.0.offline.0.name"].ShouldBe("recovering");
                health.Data["engine.0.offline.0.cause"].ShouldBe("CheckpointFailures");
                health.Data["engine.0.provisioning.0.database"].ShouldBe("provisioning");
                health.Data["engine.0.provisioning.0.rowsProcessed"].ShouldBe(1L);
                healthReads++;
                await Task.Yield();
            }
            while (clock.Elapsed < TimeSpan.FromMilliseconds(100) || healthReads < 3);

            healthReads.ShouldBeGreaterThanOrEqualTo(3);
            recoveryEntries.ShouldBe(1);
        }
        finally
        {
            release.Set();
            if (pendingCheck is not null)
            {
                await pendingCheck.WaitAsync(timeout.Token);
            }
            await ((IHost)application).StopAsync(timeout.Token);
            engine.BeforeProvisioning = null;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Inventory: cancellation is observed while a reopen holds the recovery monitor")]
    public async Task GetDatabasesAsync_CanceledDuringReopen_ShouldCancelWithoutWaitingForRecovery()
    {
        // Arrange: obtain the base sequence health enumerates, then cancel its token while an
        // actual reopen holds the monitor. Cancellation must work even with no published database.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("inventory-reopen", new());
        var database = await engine.CreateDatabaseAsync("recovering", timeout.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int recoveryEntries = 0;
        engine.BeforeProvisioning = _ =>
        {
            Interlocked.Increment(ref recoveryEntries);
            entered.TrySetResult();
            release.Wait(timeout.Token);
        };
        database.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "Inventory reopen regression.", new InvalidOperationException("Injected checkpoint failure."));
        var reopen = DedicatedTestTask.Run(async () => await engine.OpenDatabaseAsync(database.Name, timeout.Token), timeout.Token);
        Task? enumerate = null;
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var inventory = ((DatabaseEngine)engine).GetDatabasesAsync(canceled.Token);
            canceled.Cancel();

            // Act
            enumerate = DedicatedTestTask.Run(async () =>
            {
                await foreach (var item in inventory)
                {
                    throw new InvalidOperationException($"Canceled inventory yielded '{item.Name}'.");
                }
            }, timeout.Token);
            var failure = await Should.ThrowAsync<OperationCanceledException>(async () =>
                await enumerate.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token));

            // Assert
            failure.CancellationToken.ShouldBe(canceled.Token);
            recoveryEntries.ShouldBe(1);
        }
        finally
        {
            release.Set();
            await reopen.WaitAsync(timeout.Token);
            if (enumerate is not null)
            {
                try
                {
                    await enumerate.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (canceled.IsCancellationRequested)
                {
                    // Observe the test's canceled inventory after releasing a failing old implementation.
                }
            }
            engine.BeforeProvisioning = null;
        }
    }
}
