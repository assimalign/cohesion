using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Mapping;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

/// <summary>Demonstrates why a lost COMMIT acknowledgement cannot be treated as a retryable rollback.</summary>
public sealed class SqlMapperOutcomeTests
{
    /// <summary>The same missing response can mean committed or not committed; both scopes must stop replay.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Outcome: lost commit acknowledgement poisons the unit of work and store")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesAsync_LostCommitAcknowledgement_ShouldExposeUnknownOutcomeAndPreventReplay(bool commitReachesServer)
    {
        // Arrange: the decorator loses the acknowledgement before or after an actual engine COMMIT.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var faultingClient = new CommitAcknowledgementLossClient(harness.Client, commitReachesServer);
        var store = new SqlMappingStore(faultingClient);
        var work = MappingUnitOfWork.Create(store);
        SqlMapping.Register(work, new MapperParentMapper()).Add(new MapperParent { Id = 1, Name = "uncertain" });

        // Act.
        var error = await Should.ThrowAsync<MappingCommitOutcomeUnknownException>(async () =>
            await work.SaveChangesAsync(timeout.Token));

        // Assert: no acknowledgement means no definite outcome, even though server publication is atomic.
        error.Message.ShouldContain("unknown");
        faultingClient.AbortCount.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(async () => await work.SaveChangesAsync(timeout.Token));
        var anotherWork = MappingUnitOfWork.Create(store);
        SqlMapping.Register(anotherWork, new MapperParentMapper()).Add(new MapperParent { Id = 2 });
        await Should.ThrowAsync<InvalidOperationException>(async () => await anotherWork.SaveChangesAsync(timeout.Token));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token));

        // A separate connection can observe authoritative state for application reconciliation.
        // This observation is deliberately not an automatic retry or a claim of general reconciliation.
        var actual = await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token);
        actual.Count.ShouldBe(commitReachesServer ? 1 : 0);
        if (commitReachesServer)
        {
            actual[0].Name.ShouldBe("uncertain");
        }
        faultingClient.AbortCount.ShouldBe(1);
    }

    /// <summary>Aborting an active rental closes its transaction and prevents pool contamination.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Abort: active transaction is discarded before re-rental")]
    public async Task AbortAsync_ActiveTransaction_ShouldDiscardRentalAndUncommittedRows()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("BEGIN;", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO mapper_parents (Id, Name) VALUES (1, 'uncommitted');",
            cancellationToken: timeout.Token);

        // Act.
        await connection.AbortAsync();

        // Assert: the old transaction is neither visible nor reused by the next mapping save.
        connection.IsOpen.ShouldBeFalse();
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token)).ShouldBeEmpty();
        var work = MappingUnitOfWork.Create(harness.Store);
        SqlMapping.Register(work, new MapperParentMapper()).Add(new MapperParent { Id = 2, Name = "fresh transaction" });
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(1);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token))
            .ShouldHaveSingleItem().Id.ShouldBe(2);
        await connection.DisposeAsync();
    }
}
