using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlRecoveryHealthTests
{
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
