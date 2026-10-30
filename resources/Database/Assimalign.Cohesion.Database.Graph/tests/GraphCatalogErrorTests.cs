using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphCatalogErrorTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Catalog: deterministic metadata errors are caller failures")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SaveAsync_InvalidIdentityOrOwner_ShouldReportCallerError(int kind)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("catalog-errors", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "a");
        await schema.SaveLabelAsync(label, CancellationToken.None);
        await schema.CreateIndexAsync("a", "i", "p", CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            switch (kind)
            {
                case 0: await schema.SaveLabelAsync(label with { Name = "b" }, CancellationToken.None); break;
                case 1: await schema.SaveLabelAsync(label with { Id = Guid.NewGuid() }, CancellationToken.None); break;
                case 2: await schema.SavePropertyKeyAsync(new(Guid.NewGuid(), "p"), CancellationToken.None); break;
                default:
                    await database.RunAsync(session, async operation =>
                    {
                        await database.LockWriterAsync(operation, CancellationToken.None);
                        await database.Catalog.SaveIndexAsync(new(label.Id, "i", "q"), operation.Context, CancellationToken.None);
                        return true;
                    }, CancellationToken.None);
                    break;
            }
        });

        // Assert
        failure.ShouldNotBeAssignableTo<DatabaseTransactionAbortedException>();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        failure.Message.ShouldNotStartWith("CG03003:", Case.Sensitive);
        var cause = failure.InnerException.ShouldBeOfType<GraphCatalogException>();
        cause.IsConflict.ShouldBeFalse();
        cause.IsCallerError.ShouldBeTrue();
        (await schema.GetLabelsAsync(CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(label);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog: explicit schema writes refuse before catalog mutation")]
    public async Task SaveLabelAsync_ExplicitTransaction_ShouldRefuseBeforeCatalogMutation()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("catalog-snapshot", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var first = await database.CreateSessionAsync(CancellationToken.None);
        await using var second = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await second.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Fresh");
        await GraphSchema.Open(database, first).SaveLabelAsync(label, CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await GraphSchema.Open(database, second).SaveLabelAsync(label, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04025");
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog: concurrent same-name engine creations retain one identity")]
    public async Task CreateNodeAsync_ConcurrentSameName_ShouldShareCommittedDefinition()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("catalog-concurrent", new());
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var one = await database.CreateNodeAsync(first, ["Same"], cancellationToken: timeout.Token);
        var queued = database.CreateNodeAsync(second, ["Same"], cancellationToken: timeout.Token).AsTask();
        queued.IsCompleted.ShouldBeFalse();

        // Act
        await transaction.CommitAsync(timeout.Token);
        var two = await queued;

        // Assert
        one.Id.ShouldNotBe(two.Id);
        (await GraphSchema.Open(database, second).GetLabelsAsync(timeout.Token)).ShouldHaveSingleItem().Name.ShouldBe("Same");
        (await GraphCatalogIntrospectionTests.Rows(second, "MATCH (n:Same) RETURN n")).Count.ShouldBe(2);
    }
}
