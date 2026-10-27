using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class ChildRootFailureTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Boundary: child failures are coded and the session survives")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunAsync_ChildFailure_ShouldCodeAbortAndKeepSession(bool catalog, bool explicitTransaction)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("boundary-engine", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var transaction = explicitTransaction ? await session.BeginTransactionAsync(cancellationToken: CancellationToken.None) : null;
        Exception cause = catalog ? new DocumentCatalogException("Injected catalog failure.") : new StorageIOException("Injected read failure.");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => database.RunAsync<int>(session,
            _ => ValueTask.FromException<int>(cause), CancellationToken.None).AsTask());

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(catalog ? "CD06105" : "CD06104");
        failure.InnerException.ShouldBeSameAs(cause);
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await database.RunAsync(session, _ => ValueTask.FromResult(42), CancellationToken.None)).ShouldBe(42);
    }
}
