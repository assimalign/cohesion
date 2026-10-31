using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

[CollectionDefinition("Savepoint adapter wire failures", DisableParallelization = true)]
[Collection("Savepoint adapter wire failures")]
public sealed class SavepointAdapterWireTests
{
    public static IEnumerable<object[]> UndoFailures
    {
        get
        {
            foreach (var fault in SavepointUndoFault.Cases())
            {
                if ((int)fault[1] == 1) { continue; }
                yield return [false, fault[0], (int)fault[1] == 2, false];
                yield return [true, fault[0], (int)fault[1] == 2, false];
                yield return [false, fault[0], (int)fault[1] == 2, true];
                yield return [true, fault[0], (int)fault[1] == 2, true];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Wire mutations recover every undo stage through host-owned kernel savepoints on both transports (#1726)")]
    [MemberData(nameof(UndoFailures))]
    public async Task RollbackToSavepointAsync_WireMutations_ShouldRecoverExactCommit(bool tcp, int stage, bool wholeRollback, bool readCommitted)
    {
        // Arrange: these protocols have no transaction controls; the host owns the
        // server session's transaction, as in the existing transaction-failure corpus.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await BlobClientTestHarness.StartAsync(token, tcp: tcp);
        var database = harness.Database;
        await using var connection = await harness.Client.ConnectAsync(token);
        await Write("one", 0);
        var session = (BlobDatabaseSession)harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(readCommitted ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot, token);
        var context = Context(transaction);
        var coordinator = Coordinator(database!);
        await Write("one", 1);
        await Read("one", 1);
        transaction.State.ShouldBe(TransactionState.Active);
        var marker = await coordinator.CreateSavepointAsync(context, token);
        for (int i = 0; i < 100; i++) { await Write("one", (byte)(i + 2)); }
        using var fault = new SavepointUndoFault(coordinator, context, stage);

        // Act
        try
        {
            var failure = await Record.ExceptionAsync(() => coordinator.RollbackToSavepointAsync(context, marker, token).AsTask());
            failure.ShouldNotBeNull();
            fault.IsCauseOf(failure).ShouldBeTrue();
            fault.Calls.ShouldBe(stage < 4 ? 1 : stage - 3);
            var refused = await Should.ThrowAsync<DatabaseException>(() => transaction.CommitAsync(token).AsTask());
            refused.Diagnostics.ShouldContain(item => item.Code == "CK04009");
        }
        finally { fault.Disarm(); }
        if (wholeRollback) { await transaction.RollbackAsync(token); }
        else { await coordinator.RollbackToSavepointAsync(context, marker, token); }

        // Assert: another wire mutation can commit, and prefix/content are exact.
        if (stage is 2 or 3) { fault.Value.ShouldBe(7); }
        if (wholeRollback)
        {
            await using var next = await session.BeginTransactionAsync(cancellationToken: token);
            await Write("two", 9);
            await next.CommitAsync(token);
        }
        else
        {
            await Write("two", 9);
            await transaction.CommitAsync(token);
        }
        await Read("one", wholeRollback ? (byte)0 : (byte)1);
        await Read("two", 9);

        async Task Write(string name, byte value)
        {
            using var bytes = new MemoryStream(Enumerable.Repeat(value, 20_000).ToArray());
            (await connection.UploadAsync("files", name, bytes, cancellationToken: token)).ShouldBe(20_000);
        }
        async Task Read(string name, byte expected)
        {
            await using var stream = await connection.DownloadAsync("files", name, token);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, token);
            bytes.ToArray().ShouldBe(Enumerable.Repeat(expected, 20_000).ToArray());
        }
    }


    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Interrupted borrowed uploads retain the failure and never publish partial content (#1726)")]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task UploadAsync_InterruptedHostScope_ShouldAbortAndDrain(bool tcp, int ending)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        int admitted = 0, completed = 0, rounds = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await using var harness = await BlobClientTestHarness.StartAsync(token, tcp: tcp);
            await using var connection = await harness.Client.ConnectAsync(token);
            using (var original = new MemoryStream(new byte[20_000]))
            { await connection.UploadAsync("files", "one", original, cancellationToken: token); }
            var session = (BlobDatabaseSession)harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession!;
            await using var transaction = await session.BeginTransactionAsync(token);
            var context = Context(transaction);
            var coordinator = Coordinator(harness.Database);
            var marker = await coordinator.CreateSavepointAsync(context, token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var source = new GatedContentStream();
            var upload = connection.UploadAsync("files", "one", source, cancellationToken: cancellation.Token).AsTask();
            Task? end = null;

            // Act: the sender asks for its second chunk only after the server accepted the first.
            try
            {
                await source.FirstChunkAccepted.Task.WaitAsync(token);
                admitted++;
                var create = await Record.ExceptionAsync(() => coordinator.CreateSavepointAsync(context, token).AsTask());
                var release = await Record.ExceptionAsync(() => coordinator.ReleaseSavepointAsync(context, marker, token).AsTask());
                var undo = await Record.ExceptionAsync(() => coordinator.RollbackToSavepointAsync(context, marker, token).AsTask());
                foreach (var refused in new[] { create, release, undo })
                { TransactionCoordinator.GetUndoFailureCode(refused!).ShouldBe("CK04010"); }
                if (ending == 0) { cancellation.Cancel(); }
                else if (ending == 1) { harness.Factory.LastConnection!.Abort(new IOException("Injected upload disconnect")); }
                else if (ending == 2) { end = connection.DisposeAsync().AsTask(); }
                else { end = session.DisposeAsync().AsTask(); }
            }
            finally { source.Resume.TrySetResult(); }
            var failure = await Record.ExceptionAsync(async () => await upload.WaitAsync(token));
            failure.ShouldNotBeNull();
            if (end is not null) { await end.WaitAsync(token); }
            await connection.DisposeAsync();
            while (harness.Server.Sessions.Count != 0) { await Task.Delay(1, token); }

            // Assert: teardown ran the whole abort before buffered stream disposal could publish.
            var commit = await Should.ThrowAsync<DatabaseException>(() => transaction.CommitAsync(token).AsTask());
            commit.Diagnostics.ShouldContain(item => item.Code == "CB04001");
            context.State.ShouldNotBe(TransactionState.Active);
            await using var input = await harness.Container.OpenReadAsync("one", token);
            using var bytes = new MemoryStream();
            await input.CopyToAsync(bytes, token);
            bytes.ToArray().ShouldBe(new byte[20_000]);
            completed++;
            rounds++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8 || admitted < 8 || completed < 8)
            && elapsed.Elapsed < TimeSpan.FromSeconds(45));
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        admitted.ShouldBe(rounds);
        completed.ShouldBe(rounds);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(BlobDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Context")]
    private static extern TransactionContext Context(BlobDatabaseTransaction transaction);
}
