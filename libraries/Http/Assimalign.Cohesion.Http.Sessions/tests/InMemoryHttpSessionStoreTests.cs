using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class InMemoryHttpSessionStoreTests
{
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(20);

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: Set then Get should round-trip the payload")]
    public async Task SetAsync_GetAsync_ShouldRoundTripPayload()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        byte[] payload = [1, 2, 3, 4];

        // Act
        await store.SetAsync("id", payload, _idleTimeout);
        byte[]? loaded = await store.GetAsync("id");

        // Assert
        loaded.ShouldBe(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: Get of an unknown id should return null")]
    public async Task GetAsync_UnknownId_ShouldReturnNull()
    {
        InMemoryHttpSessionStore store = new();

        (await store.GetAsync("missing")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: Remove should evict the entry")]
    public async Task RemoveAsync_ShouldEvictEntry()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        await store.SetAsync("id", [9], _idleTimeout);

        // Act
        await store.RemoveAsync("id");

        // Assert
        (await store.GetAsync("id")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: Set is an unconditional overwrite (last-commit-wins)")]
    public async Task SetAsync_Twice_ShouldReplacePayloadWholesale()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();

        // Act
        await store.SetAsync("id", [1], _idleTimeout);
        await store.SetAsync("id", [2, 2], _idleTimeout);
        byte[]? loaded = await store.GetAsync("id");

        // Assert
        loaded.ShouldBe(new byte[] { 2, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: An entry should expire after its idle window elapses")]
    public async Task GetAsync_AfterIdleWindowElapses_ShouldReturnNull()
    {
        // Arrange
        MutableTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        InMemoryHttpSessionStore store = new(clock);
        await store.SetAsync("id", [7], _idleTimeout);

        // Act
        clock.Advance(_idleTimeout + TimeSpan.FromSeconds(1));
        byte[]? loaded = await store.GetAsync("id");

        // Assert
        loaded.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: Access should slide the idle window (renew-on-access)")]
    public async Task GetAsync_WithinWindow_ShouldSlideExpiration()
    {
        // Arrange
        MutableTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        InMemoryHttpSessionStore store = new(clock);
        await store.SetAsync("id", [7], _idleTimeout);

        // Act — access just before expiry renews the window, then advance again
        clock.Advance(_idleTimeout - TimeSpan.FromMinutes(1));
        (await store.GetAsync("id")).ShouldNotBeNull(); // renews to now + IdleTimeout

        clock.Advance(_idleTimeout - TimeSpan.FromMinutes(1)); // still inside the renewed window
        byte[]? stillAlive = await store.GetAsync("id");

        // Assert
        stillAlive.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: Refresh should slide the window without changing the payload")]
    public async Task RefreshAsync_ShouldSlideWindowKeepingPayload()
    {
        // Arrange
        MutableTimeProvider clock = new(DateTimeOffset.UnixEpoch);
        InMemoryHttpSessionStore store = new(clock);
        await store.SetAsync("id", [5], _idleTimeout);

        // Act
        clock.Advance(_idleTimeout - TimeSpan.FromMinutes(1));
        await store.RefreshAsync("id", _idleTimeout);
        clock.Advance(_idleTimeout - TimeSpan.FromMinutes(1));
        byte[]? loaded = await store.GetAsync("id");

        // Assert
        loaded.ShouldBe(new byte[] { 5 });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - InMemoryStore: A null or empty id should be rejected")]
    public async Task Operations_NullOrEmptyId_ShouldThrow()
    {
        InMemoryHttpSessionStore store = new();

        await Should.ThrowAsync<ArgumentException>(async () => await store.GetAsync(""));
        await Should.ThrowAsync<ArgumentException>(async () => await store.SetAsync("", [1], _idleTimeout));
    }
}
