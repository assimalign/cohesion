using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class ChildRootFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Boundary: upload state failures are coded and preserve the session")]
    public async Task RunAsync_UploadStateFailure_ShouldReturnCodedError()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("upload-errors", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var cause = new InvalidOperationException("The blob upload has failed and cannot be published.");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => database.RunAsync<int>(session,
            _ => ValueTask.FromException<int>(cause), CancellationToken.None).AsTask());

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB007");
        failure.InnerException.ShouldBeSameAs(cause);
        (await session.CreateContainerAsync("after", CancellationToken.None)).Name.ShouldBe("after");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Boundary: child failures are coded and the session survives")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunAsync_ChildFailure_ShouldCodeAbortAndKeepSession(bool catalog, bool explicitTransaction)
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("boundary-engine", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var transaction = explicitTransaction ? await session.BeginTransactionAsync(cancellationToken: CancellationToken.None) : null;
        Exception cause = catalog ? new BlobCatalogException("Injected catalog failure.") : new StorageIOException("Injected read failure.");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => database.RunAsync<int>(session,
            _ => ValueTask.FromException<int>(cause), CancellationToken.None).AsTask());

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(catalog ? "COHDBB006" : "COHDBB005");
        failure.InnerException.ShouldBeSameAs(cause);
        if (transaction is not null)
        {
            transaction.State.ShouldBe(TransactionState.Faulted);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await database.RunAsync(session, _ => ValueTask.FromResult(42), CancellationToken.None)).ShouldBe(42);
    }
}
