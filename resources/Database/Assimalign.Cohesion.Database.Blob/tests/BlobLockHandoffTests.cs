using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[Collection(nameof(BlobLockHandoffCollection))]
public sealed class BlobLockHandoffTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Blob] - Lock handoff: tracked committed writers cannot hide catalog changes")]
    [InlineData("CreateContainer")]
    [InlineData("DropContainerIdentity")]
    [InlineData("DropContainerContents")]
    [InlineData("UploadMetadata")]
    [InlineData("DeleteBlob")]
    [InlineData("UploadToDroppedContainer")]
    [InlineData("DeleteFromDroppedContainer")]
    [InlineData("ContainerMetadata")]
    public async Task Mutate_CommittedWriterStillTracked_ShouldRejectStaleSnapshot(string path)
    {
        // Arrange: exercise each gated handoff for at least a second and at least twice.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = BlobDatabaseEngine.Create("blob-handoff", new());
        var elapsed = Stopwatch.StartNew();
        int blocked = 0, refused = 0;
        do
        {
            await using var database = await engine.CreateDatabaseAsync("handoff" + blocked, token);
            await using var setup = await database.CreateSessionAsync(token);
            if (path != "CreateContainer")
            {
                var seed = await setup.CreateContainerAsync("c", token);
                if (path is "UploadMetadata" or "DeleteBlob") { await WriteAsync(seed, "old", token); }
            }
            await using var first = await database.CreateSessionAsync(token);
            await using var second = await database.CreateSessionAsync(token);
            await using var writer = await first.BeginTransactionAsync(IsolationLevel.Snapshot, token);
            await using var stale = await second.BeginTransactionAsync(IsolationLevel.Snapshot, token);
            BlobContainer? waiting = path == "CreateContainer" ? null : await second.GetContainerAsync("c", token);
            var original = path == "CreateContainer" ? await first.CreateContainerAsync("c", token) : await first.GetContainerAsync("c", token);
            if (path is "DropContainerIdentity" or "UploadToDroppedContainer" or "DeleteFromDroppedContainer")
            { await first.DropContainerAsync("c", token); }
            else if (path == "ContainerMetadata")
            {
                await database.LockWriterAsync(writer.Context, token);
                await database.Catalog.SaveContainerAsync(original.Metadata with
                    { Owner = DatabaseObjectOwner.Schema, OwningSchema = "schema" }, writer.Context, token);
            }
            else { await WriteAsync(original, "committed", token); }

            var pending = MutateAsync();
            pending.IsCompleted.ShouldBeFalse();
            blocked++;
            using var gate = new CommitEventGate(database.Name, writer.Context.Sequence.Value);
            var committing = DedicatedTestTask.Run(async () => await writer.CommitAsync(token), token);
            try
            {
                // Act: the waiter finishes while commit is held before untracking.
                await gate.Entered.WaitAsync(token);
                database.Coordinator.GetOpenContexts().Any(context => context.Sequence == writer.Context.Sequence &&
                    context.State == TransactionState.Committed).ShouldBeTrue();
                await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending.WaitAsync(token));
                refused++;
            }
            finally { gate.Release(); await committing.WaitAsync(token); }

            // Assert: the gate ran, the stale transaction aborted, and A's data survives.
            gate.Calls.ShouldBe(1);
            gate.TimedOut.ShouldBeFalse();
            stale.State.ShouldBe(TransactionState.Faulted);
            await stale.RollbackAsync(token);
            if (path is "CreateContainer" or "DropContainerContents" or "UploadMetadata" or "DeleteBlob")
            {
                var visible = await setup.GetContainerAsync("c", token);
                await using (var stream = await visible.OpenReadAsync("original", token))
                {
                    using var reader = new StreamReader(stream);
                    (await reader.ReadToEndAsync(token)).ShouldBe("committed");
                }
                (await visible.GetPropertiesAsync("original", token)).ShouldNotBeNull().ContentType.ShouldBe("text/committed");
            }

            async Task MutateAsync()
            {
                switch (path)
                {
                    case "CreateContainer": await second.CreateContainerAsync("c", token); break;
                    case "DropContainerIdentity":
                    case "DropContainerContents": await second.DropContainerAsync("c", token); break;
                    case "DeleteBlob":
                    case "DeleteFromDroppedContainer": await waiting!.DeleteAsync("original", token); break;
                    default: await WriteAsync(waiting!, "stale", token); break;
                }
            }
        } while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || blocked < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(20));
        blocked.ShouldBeGreaterThanOrEqualTo(2);
        refused.ShouldBe(blocked);
    }

    private static async Task WriteAsync(BlobContainer container, string value, CancellationToken token)
    {
        await using var stream = await container.OpenWriteAsync("original", new BlobWriteOptions { ContentType = "text/" + value }, token);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token);
    }
}
