using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentContentFailureCollection))]
public sealed class DocumentContentFailureConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Content: Failure and cancellation race rollback and disposal without bypassing the end gate (#1244)")]
    public async Task PutAsync_ChunkFailureRacesTransactionEnd_ShouldReleaseAndUndoEveryWrite()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("chunk-race", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using (var setup = await database.CreateSessionAsync(CancellationToken.None))
        {
            await setup.CreateCollectionAsync("items", cancellationToken: CancellationToken.None);
        }
        byte[] bytes = Encoding.UTF8.GetBytes("\"" + new string('x', 2 * DocumentChunkCodec.PayloadSize) + "\"");
        int rounds = 0, overlaps = 0, failures = 0, cancellations = 0;
        int[] endings = new int[3];
        var elapsed = Stopwatch.StartNew();
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act: run for at least two seconds and until all six end/failure combinations ran.
        while (elapsed.Elapsed < TimeSpan.FromSeconds(2) || rounds < 6)
        {
            ceiling.Token.ThrowIfCancellationRequested();
            int endKind = rounds % 3;
            bool cancel = rounds % 6 >= 3;
            await using var session = await database.CreateSessionAsync(ceiling.Token);
            var collection = await session.GetCollectionAsync("items", ceiling.Token);
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: ceiling.Token);
            await collection.PutAsync(session, "pending", "{}"u8.ToArray(), cancellationToken: ceiling.Token);
            using var cancellation = new CancellationTokenSource();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int chunks = 0;
            database.DataStorage.BeforeContentChunkWrite = () =>
            {
                if (++chunks != 2) { return; }
                entered.SetResult();
                release.Wait(ceiling.Token);
                if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                throw new InvalidOperationException("Injected content chunk failure.");
            };
            // Capture the expected failure so teardown can always drain the writer task.
            var writing = Task.Run(async () =>
            {
                try
                {
                    await collection.PutAsync(session, "failed", bytes, cancellationToken: cancellation.Token);
                    return (Exception?)null;
                }
                catch (Exception error) { return error; }
            }, ceiling.Token);
            Task? ending = null;
            try
            {
                await entered.Task.WaitAsync(ceiling.Token);
                ending = endKind switch
                {
                    0 => transaction.RollbackAsync(ceiling.Token).AsTask(),
                    1 => transaction.DisposeAsync().AsTask(),
                    _ => session.DisposeAsync().AsTask(),
                };
                while (transaction.AcceptsStatements)
                {
                    ceiling.Token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                ending.IsCompleted.ShouldBeFalse();
                overlaps++;
            }
            finally
            {
                release.Set();
                try
                {
                    var failure = await writing.WaitAsync(ceiling.Token);
                    failure.ShouldNotBeNull();
                    if (cancel) { failure.ShouldBeAssignableTo<OperationCanceledException>(); cancellations++; }
                    else { failure.ShouldBeOfType<InvalidOperationException>(); failures++; }
                    if (ending is not null) { await ending.WaitAsync(ceiling.Token); }
                }
                finally { database.DataStorage.BeforeContentChunkWrite = null; }
            }
            chunks.ShouldBe(2);
            await using var observer = await database.CreateSessionAsync(ceiling.Token);
            var observed = await observer.GetCollectionAsync("items", ceiling.Token);
            (await observed.GetAsync(observer, "pending", ceiling.Token)).ShouldBeNull();
            (await observed.GetAsync(observer, "failed", ceiling.Token)).ShouldBeNull();
            // A later writer proves that both operation admission and the writer lock were released.
            await observed.PutAsync(observer, "probe", "{}"u8.ToArray(), cancellationToken: ceiling.Token);
            endings[endKind]++;
            rounds++;
        }

        // Assert
        rounds.ShouldBeGreaterThanOrEqualTo(6);
        overlaps.ShouldBe(rounds);
        failures.ShouldBeGreaterThan(0);
        cancellations.ShouldBeGreaterThan(0);
        endings.ShouldAllBe(count => count > 0);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }
}
