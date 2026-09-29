using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobOwnershipIntrospectionTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Ownership: exposes catalog authority through existing container handles")]
    public async Task GetOwnership_WithBothAuthorities_ShouldReadCatalog()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("test");
        await database.CreateContainerAsync("adhoc");
        await SaveManagedAsync(database, "managed", "MediaSchema");

        var count = 0;
        await foreach (var container in database.GetContainersAsync())
        {
            var ownership = await container.GetOwnershipAsync();
            ownership.Count.ShouldBe(2);
            ownership["OWNER"].ShouldBe(container.Name == "managed" ? DatabaseObjectOwner.Schema : DatabaseObjectOwner.Adhoc);
            ownership["OWNING_SCHEMA"].ShouldBe(container.Name == "managed" ? "MediaSchema" : null);
            count++;
        }
        count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Ownership: dictionary mutations are refused")]
    public async Task GetOwnership_WhenMutationAttempted_ShouldRefuseAndPreserveCatalog()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        var ownership = (IDictionary<string, object?>)await container.GetOwnershipAsync();

        ownership.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => ownership["OWNER"] = DatabaseObjectOwner.Schema);
        Should.Throw<NotSupportedException>(() => ownership.Add("OWNING_SCHEMA", "Injected"));
        Should.Throw<NotSupportedException>(() => ownership.Remove("OWNER"));
        Should.Throw<NotSupportedException>(() => ownership.Clear());
        (await container.GetOwnershipAsync())["OWNER"].ShouldBe(DatabaseObjectOwner.Adhoc);
        (await container.GetOwnershipAsync())["OWNING_SCHEMA"].ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Ownership: scope stays with the session database")]
    public async Task GetOwnership_WithSameContainerNames_ShouldRemainDatabaseScoped()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var own = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("own");
        var other = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("other");
        await SaveManagedAsync(own, "files", "OwnSchema");
        await SaveManagedAsync(other, "files", "OtherSchema");
        await using var session = await own.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;

        (await (await scoped.GetContainerAsync("files")).GetOwnershipAsync())["OWNING_SCHEMA"].ShouldBe("OwnSchema");
        await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetContainerAsync("other/files"));
        (await (await other.GetContainerAsync("files")).GetOwnershipAsync())["OWNING_SCHEMA"].ShouldBe("OtherSchema");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Ownership: respects transaction visibility and reads fresh metadata")]
    [InlineData(IsolationLevel.Snapshot, "Original")]
    [InlineData(IsolationLevel.ReadCommitted, "Updated")]
    public async Task GetOwnership_AfterCatalogChange_ShouldUseOperationSnapshot(IsolationLevel isolationLevel, string expected)
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("test");
        await SaveManagedAsync(database, "files", "Original");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync(isolationLevel);
        var container = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var first = await container.GetOwnershipAsync();

        await SaveManagedAsync(database, "files", "Updated");

        (await container.GetOwnershipAsync())["OWNING_SCHEMA"].ShouldBe(expected);
        first["OWNING_SCHEMA"].ShouldBe("Original");
        await transaction.CommitAsync();
        (await container.GetOwnershipAsync())["OWNING_SCHEMA"].ShouldBe("Updated");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Ownership: rejects canceled reads, stale handles and disposed sessions")]
    public async Task GetOwnership_WhenHandleUnavailable_ShouldReject()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await container.GetOwnershipAsync(cancellation.Token));
        await database.DropContainerAsync("files");
        await database.CreateContainerAsync("files");
        await Should.ThrowAsync<DatabaseException>(async () => await container.GetOwnershipAsync());

        await using var session = await database.CreateSessionAsync();
        var scoped = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        await session.DisposeAsync();
        await Should.ThrowAsync<DatabaseException>(async () => await scoped.GetOwnershipAsync());
    }

    private static async Task SaveManagedAsync(BlobDatabaseInstance database, string name, string schema)
    {
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var previous = database.Catalog.FindContainer(name, context.Snapshot);
        await database.Catalog.SaveContainerAsync(new BlobContainerMetadata(previous?.Id ?? Guid.NewGuid(), name,
            DatabaseObjectOwner.Schema, schema), context);
        await database.Coordinator.CommitAsync(context);
    }
}
