using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;


namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class StorageOpenFailureTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Open: data and catalog I/O failures are coded and permit retry")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OpenDatabaseAsync_StorageReadFailure_ShouldCodeAndReleaseFileSets(bool catalog, bool rawIo)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        await using var engine = KeyValueDatabaseEngine.Create("open-failures", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using (var session = await database.CreateSessionAsync(CancellationToken.None))
        {
            await database.PutAsync(session, "key"u8.ToArray(), "acknowledged"u8.ToArray(), cancellationToken: CancellationToken.None);
        }
        await database.DisposeAsync();
        Exception cause = rawIo ? new IOException("Injected device failure.") : new StorageIOException("Injected storage failure.");
        strategy.OpenFailure = isCatalog => isCatalog == catalog ? cause : null;

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("test", CancellationToken.None).AsTask());

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV06105");
        failure.InnerException.ShouldBeSameAs(cause);
        failure.Message.ShouldContain("test", Case.Sensitive);
        engine.TryGetDatabase("test", out _).ShouldBeFalse();
        strategy.OpenFailure = null;
        var reopened = await engine.OpenDatabaseAsync("test", CancellationToken.None);
        reopened.Name.ShouldBe((DatabaseName)"test");
        await using var reader = await reopened.CreateSessionAsync(CancellationToken.None);
        var entry = (await reopened.GetAsync(reader, "key"u8.ToArray(), CancellationToken.None)).ShouldNotBeNull();
        entry.Value.ToArray().ShouldBe("acknowledged"u8.ToArray());
    }
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Create: wrapped storage subclasses carry the model code before publication")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateDatabaseAsync_WrappedStorageFailure_ShouldCodeAndLeaveUnpublished(bool catalog)
    {
        // Arrange
        var cause = new AggregateException(new InvalidOperationException("Other failure."), new StorageTransactionException("Injected create failure."));
        var strategy = new FaultInjectingJournalStorageStrategy { CreateFailure = isCatalog => isCatalog == catalog ? cause : null };
        await using var engine = KeyValueDatabaseEngine.Create("create-failures", new() { StorageStrategy = strategy });

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => engine.CreateDatabaseAsync("test", CancellationToken.None).AsTask());

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CV06105");
        failure.InnerException.ShouldBeSameAs(cause);
        engine.TryGetDatabase("test", out _).ShouldBeFalse();
    }

}
