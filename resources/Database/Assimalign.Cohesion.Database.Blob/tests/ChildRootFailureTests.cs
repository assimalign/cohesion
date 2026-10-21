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
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Boundary: unrelated invalid operations pass through and preserve the session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_UnrelatedInvalidOperation_ShouldPassThrough(bool disposed)
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("upload-errors", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        InvalidOperationException cause = disposed ? new ObjectDisposedException("upload") : new InvalidOperationException("Unrelated operation failure.");

        // Act
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => database.RunAsync<int>(session,
            _ => ValueTask.FromException<int>(cause), CancellationToken.None).AsTask());

        // Assert
        failure.ShouldBeSameAs(cause);
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
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Upload: writes after rollback are coded at the public stream")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_UploadRolledBack_ShouldReturnUploadCode(bool asyncWrite)
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("rolled-upload", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var files = await session.CreateContainerAsync("files", CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        var upload = await files.OpenWriteAsync("one", cancellationToken: CancellationToken.None);
        await upload.WriteAsync(new byte[1], CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (asyncWrite) { await upload.WriteAsync(new byte[1], CancellationToken.None); }
            else { upload.Write(new byte[1]); }
        });
        var disposal = await Should.ThrowAsync<DatabaseException>(() => upload.DisposeAsync().AsTask());

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB007");
        disposal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB007");
        (await files.GetPropertiesAsync("one", CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Upload: a failed write prevents every later write and publication")]
    public async Task WriteAsync_UploadWriteFailed_ShouldRefuseReuse()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("failed-upload", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var files = await session.CreateContainerAsync("files", CancellationToken.None);
        var upload = await files.OpenWriteAsync("one", cancellationToken: CancellationToken.None);
        await upload.WriteAsync(new byte[1000], CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => upload.WriteAsync(new byte[1], canceled.Token).AsTask());
        var failure = await Should.ThrowAsync<DatabaseException>(() => upload.WriteAsync(new byte[1], CancellationToken.None).AsTask());
        await upload.DisposeAsync();

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBB007");
        (await files.GetPropertiesAsync("one", CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Upload: database disposal during an open upload preserves ObjectDisposedException")]
    public async Task DisposeAsync_DatabaseClosedDuringUpload_ShouldPassThroughObjectDisposed()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("disposed-upload", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var files = await session.CreateContainerAsync("files", CancellationToken.None);
        var upload = await files.OpenWriteAsync("one", cancellationToken: CancellationToken.None);
        await upload.WriteAsync(new byte[1], CancellationToken.None);
        await database.DisposeAsync();

        // Act
        var failure = await Should.ThrowAsync<ObjectDisposedException>(() => upload.WriteAsync(new byte[1], CancellationToken.None).AsTask());

        // Assert
        failure.ShouldNotBeAssignableTo<DatabaseException>();
        await Should.ThrowAsync<DatabaseException>(() => upload.DisposeAsync().AsTask());
    }

}
