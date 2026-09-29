using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobReadCommittedTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - ReadCommitted: Should pin a stream snapshot across an earlier writer commit and purge")]
    public async Task ReadCommittedStream_ShouldPinItsStatementSnapshot()
    {
        await using var engine = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions());
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("statement-pin");
        var container = await database.CreateContainerAsync("files");
        byte[] original = new byte[40_000];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i * 19);
        }
        await Write(container, original);

        await using var writerSession = await database.CreateSessionAsync();
        await using var writer = await writerSession.BeginTransactionAsync();
        var writerContainer = await ((IBlobDatabase)writerSession.Database).GetContainerAsync("files");
        byte[] replacement = new byte[50_000];
        await Write(writerContainer, replacement);

        // Writer W predates reader R. R must see the original while W is active.
        // After W commits, a refreshed R snapshot would let purge pass W's deleter.
        await using var readerSession = await database.CreateSessionAsync();
        await using var reader = await readerSession.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var readerContainer = await ((IBlobDatabase)readerSession.Database).GetContainerAsync("files");
        var stream = await readerContainer.OpenReadAsync("item");
        using var downloaded = new MemoryStream();
        byte[] first = new byte[1];
        (await stream.ReadAsync(first)).ShouldBe(1);
        downloaded.Write(first);
        await writer.CommitAsync();
        database.Coordinator.RunVersionPurgePass(default).ShouldBe(0);
        await stream.CopyToAsync(downloaded);
        downloaded.ToArray().ShouldBe(original);
        await stream.DisposeAsync();

        // Completing the statement releases its pin; the next statement refreshes.
        database.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        (await readerContainer.GetPropertiesAsync("item"))!.Value.Length.ShouldBe(replacement.Length);
        await reader.RollbackAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Session: Should reject overlapping streams without aborting the first upload")]
    public async Task Session_ShouldRejectOverlappingStreamsAndPermitSequentialUploads()
    {
        await using var engine = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("stream-guard");
        await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var container = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        var first = await container.OpenWriteAsync("item");
        await first.WriteAsync("first"u8.ToArray());
        await Should.ThrowAsync<DatabaseException>(async () => await container.OpenWriteAsync("item"));
        await Should.ThrowAsync<DatabaseException>(async () => await container.GetPropertiesAsync("item"));
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        transaction.State.ShouldBe(TransactionState.Active);
        await first.DisposeAsync();

        await Write(container, "second"u8.ToArray());
        await transaction.CommitAsync();
        var committedContainer = await database.GetContainerAsync("files");
        await using var content = await committedContainer.OpenReadAsync("item");
        using var copied = new MemoryStream();
        await content.CopyToAsync(copied);
        copied.ToArray().ShouldBe("second"u8.ToArray());
    }

    private static async Task Write(IBlobContainer container, byte[] content)
    {
        await using var stream = await container.OpenWriteAsync("item");
        await stream.WriteAsync(content);
    }
}
