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

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

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
                yield return [false, fault[0], (int)fault[1]];
                yield return [true, fault[0], (int)fault[1]];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [KeyValuePair.Client] - Wire mutations recover every undo stage through host-owned kernel savepoints on both transports (#1726)")]
    [MemberData(nameof(UndoFailures))]
    public async Task RollbackToSavepointAsync_WireMutations_ShouldRecoverExactCommit(bool tcp, int stage, int recovery)
    {
        // Arrange: these protocols have no transaction controls; the host owns the
        // server session's transaction, as in the existing transaction-failure corpus.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await KeyValueClientTestHarness.StartAsync(tcp: tcp);
        harness.Engine.TryGetDatabase(KeyValueClientTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var connection = await harness.Client.ConnectAsync(token);
        await Write("one", 0);
        var session = (KeyValueDatabaseSession)harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: token);
        var context = Context(transaction);
        var coordinator = Coordinator(database!);
        var outer = await coordinator.CreateSavepointAsync(context, token);
        await Write("one", 1);
        var marker = await coordinator.CreateSavepointAsync(context, token);
        for (int i = 0; i < 100; i++) { await Write("one", (byte)(i + 2)); }
        using var fault = new SavepointUndoFault(coordinator, context, stage);
        if (stage >= 7)
        {
            for (int i = 0; i < 100; i++) { await Write("one", (byte)(i + 2)); }
        }

        // Act
        try
        {
            var failure = await Record.ExceptionAsync(() => coordinator.RollbackToSavepointAsync(context, marker, token).AsTask());
            failure.ShouldNotBeNull();
            fault.IsCauseOf(failure).ShouldBeTrue();
            fault.Calls.ShouldBe(fault.ExpectedCalls);
            var refused = await Should.ThrowAsync<DatabaseException>(() => transaction.CommitAsync(token).AsTask());
            refused.Diagnostics.ShouldContain(item => item.Code == "CK04009");
        }
        finally { fault.Disarm(); }
        if (recovery == 2) { await transaction.RollbackAsync(token); }
        else { await coordinator.RollbackToSavepointAsync(context, recovery == 1 ? outer : marker, token); }

        // Assert: another wire mutation can commit, and prefix/content are exact.
        if (stage is 2 or 3 or 9 or 10) { fault.Value.ShouldBe(7); }
        if (stage is < 4 or >= 7) { fault.Calls.ShouldBe(2); }
        if (recovery == 2)
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
        await Read("one", recovery != 0 ? (byte)0 : (byte)1);
        await Read("two", 9);

        async Task Write(string name, byte value)
        {
            await connection.PutAsync(System.Text.Encoding.UTF8.GetBytes(name), new byte[] { value }, token);
        }
        async Task Read(string name, byte expected)
        {
            (await connection.GetAsync(System.Text.Encoding.UTF8.GetBytes(name), token))!.Value.Value.ToArray().ShouldBe(new byte[] { expected });
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(KeyValueDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Context")]
    private static extern TransactionContext Context(KeyValueDatabaseTransaction transaction);
}
