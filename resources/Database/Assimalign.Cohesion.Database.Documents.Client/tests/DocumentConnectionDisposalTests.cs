using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Protocol;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

[Collection(nameof(DocumentClientEventSourceCollection))]
public sealed class DocumentConnectionDisposalTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - A Put paused at QueryStart cannot use a later rental")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_PutPausedBeforeRegistration_ShouldProtectNextRental(bool abort)
    {
        // Arrange: the harness fixes MaxPoolSize at one. The event listener gates the exact
        // gap between the wrapper's first disposed check and shared exchange registration.
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var old = await harness.Client.ConnectAsync(harness.Token);
        await old.CreateCollectionAsync("items", harness.Token);
        using var listener = new PausedQueryListener(harness.Token);
        Task<Document> put = DedicatedTestTask.Run(async () =>
            await old.PutAsync("items", "late", "{}"u8.ToArray(), cancellationToken: harness.Token), harness.Token);
        await listener.Paused.WaitAsync(harness.Token);

        // Act: return/discard and re-rent before allowing the stale call to register.
        try
        {
            if (abort) { await old.AbortAsync(); }
            else { await old.DisposeAsync(); }
            await using var next = await harness.Client.ConnectAsync(harness.Token);
            listener.Resume();

            // Assert
            await Should.ThrowAsync<ObjectDisposedException>(async () => await put);
            listener.PauseCount.ShouldBe(1);
            old.IsOpen.ShouldBeFalse();
            await old.DisposeAsync();
            await old.AbortAsync();
            next.IsOpen.ShouldBeTrue();
            (await next.GetAsync("items", "late", harness.Token)).ShouldBeNull();
            (await next.GetCollectionAsync("items", harness.Token)).ShouldBe("items");
        }
        finally
        {
            listener.Resume();
            try { await put; }
            catch (ObjectDisposedException) { }
        }
    }
}
