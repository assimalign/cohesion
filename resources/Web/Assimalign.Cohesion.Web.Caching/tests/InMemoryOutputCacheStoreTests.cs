using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// Unit coverage for the default in-memory output-cache store: round-trip, absent-key miss, absolute
/// time-to-live expiry over a manual clock, tag eviction (including the tag-index self-clean), and the
/// size limit driven by per-entry accounting.
/// </summary>
public class InMemoryOutputCacheStoreTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static OutputCacheEntry Entry(byte[] body, TimeSpan validFor, params string[] tags)
        => new(
            HttpStatusCode.Ok,
            Array.Empty<OutputCacheHeader>(),
            body,
            Epoch,
            validFor,
            tags,
            Array.Empty<string>());

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Store: A stored entry round-trips by key")]
    public async Task SetAsync_ThenGet_ShouldReturnStoredEntry()
    {
        // Arrange
        using InMemoryOutputCacheStore store = new();
        OutputCacheEntry entry = Entry(new byte[] { 1, 2, 3 }, TimeSpan.FromMinutes(5));

        // Act
        await store.SetAsync("key", entry);
        OutputCacheEntry? result = await store.GetAsync("key");

        // Assert
        result.ShouldNotBeNull();
        result!.Body.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Store: An absent key returns null")]
    public async Task GetAsync_MissingKey_ShouldReturnNull()
    {
        // Arrange
        using InMemoryOutputCacheStore store = new();

        // Act
        OutputCacheEntry? result = await store.GetAsync("absent");

        // Assert
        result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Store: An entry past its time-to-live is absent")]
    public async Task GetAsync_PastTimeToLive_ShouldReturnNull()
    {
        // Arrange
        ManualTimeProvider time = new(Epoch);
        using InMemoryOutputCacheStore store = new(InMemoryOutputCacheStore.DefaultSizeLimit, time);
        await store.SetAsync("key", Entry(new byte[] { 9 }, TimeSpan.FromSeconds(30)));

        // Act
        time.Advance(TimeSpan.FromSeconds(31));
        OutputCacheEntry? result = await store.GetAsync("key");

        // Assert
        result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Store: EvictByTag removes every entry carrying the tag")]
    public async Task EvictByTagAsync_ShouldRemoveTaggedEntries()
    {
        // Arrange
        using InMemoryOutputCacheStore store = new();
        await store.SetAsync("a", Entry(new byte[] { 1 }, TimeSpan.FromMinutes(5), "products"));
        await store.SetAsync("b", Entry(new byte[] { 2 }, TimeSpan.FromMinutes(5), "products"));
        await store.SetAsync("c", Entry(new byte[] { 3 }, TimeSpan.FromMinutes(5), "orders"));

        // Act
        await store.EvictByTagAsync("products");

        // Assert
        (await store.GetAsync("a")).ShouldBeNull();
        (await store.GetAsync("b")).ShouldBeNull();
        (await store.GetAsync("c")).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Store: A re-tagged entry is not re-evicted by its old tag")]
    public async Task EvictByTagAsync_AfterRetag_ShouldNotEvictUnderOldTag()
    {
        // Arrange
        using InMemoryOutputCacheStore store = new();
        await store.SetAsync("k", Entry(new byte[] { 1 }, TimeSpan.FromMinutes(5), "old"));
        await store.SetAsync("k", Entry(new byte[] { 2 }, TimeSpan.FromMinutes(5), "new"));

        // Act
        await store.EvictByTagAsync("old");

        // Assert — the replacement carries only "new", so the "old" purge must not touch it.
        OutputCacheEntry? result = await store.GetAsync("k");
        result.ShouldNotBeNull();
        result!.Body.ShouldBe(new byte[] { 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Store: A body larger than the whole size limit is declined")]
    public async Task SetAsync_OversizedEntry_ShouldBeDeclined()
    {
        // Arrange — a 1 KiB store cannot hold a 4 KiB body.
        using InMemoryOutputCacheStore store = new(1024);
        OutputCacheEntry entry = Entry(new byte[4096], TimeSpan.FromMinutes(5));

        // Act
        await store.SetAsync("big", entry);

        // Assert
        (await store.GetAsync("big")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Store: A null or empty key is rejected")]
    public async Task GetAsync_EmptyKey_ShouldThrow()
    {
        // Arrange
        using InMemoryOutputCacheStore store = new();

        // Act / Assert
        await Should.ThrowAsync<ArgumentException>(async () => await store.GetAsync(string.Empty));
    }
}
