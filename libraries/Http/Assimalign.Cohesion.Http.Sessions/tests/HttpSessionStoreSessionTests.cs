using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// Exercises the store-backed session: load/commit round-trips through the store, the
/// commit-only-when-modified rule, the last-commit-wins concurrency contract, and
/// id reassignment for session-id regeneration.
/// </summary>
public class HttpSessionStoreSessionTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - CreateSession: A new session should remain unloaded until requested")]
    public async Task CreateSession_StoredState_ShouldRemainUnloadedUntilLoadAsync()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        IHttpStoredSession writer = store.CreateSession("id", IdleTimeout);
        writer.SetString("user", "alice");
        await writer.CommitAsync();

        // Act
        IHttpStoredSession session = store.CreateSession("id", IdleTimeout);

        // Assert
        session.IsAvailable.ShouldBeFalse();
        session.Keys.ShouldBeEmpty();
        await session.LoadAsync();
        session.IsAvailable.ShouldBeTrue();
        session.GetString("user").ShouldBe("alice");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Sessions] - CreateSession: Nonpositive idle windows should fail before use")]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateSession_NonpositiveIdleTimeout_ShouldThrow(int seconds)
    {
        // Arrange
        InMemoryHttpSessionStore store = new();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => store.CreateSession("id", TimeSpan.FromSeconds(seconds)));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - StoreSession: Commit then load on a fresh session should round-trip state")]
    public async Task CommitAsync_LoadAsync_ShouldRoundTripThroughStore()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        IHttpStoredSession writer = store.CreateSession("id", IdleTimeout);
        writer.SetString("user", "alice");
        writer.SetInt32("count", 3);

        // Act
        await writer.CommitAsync();

        IHttpStoredSession reader = store.CreateSession("id", IdleTimeout);
        await reader.LoadAsync();

        // Assert
        reader.IsAvailable.ShouldBeTrue();
        reader.GetString("user").ShouldBe("alice");
        reader.GetInt32("count").ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - StoreSession: Loading an unknown id yields an empty, available session")]
    public async Task LoadAsync_UnknownId_ShouldYieldEmptyAvailableSession()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        HttpSessionStoreSession session = new("new-id", store, IdleTimeout);

        // Act
        await session.LoadAsync();

        // Assert
        session.IsAvailable.ShouldBeTrue();
        session.Keys.ShouldBeEmpty();
        session.IsModified.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - StoreSession: An unmodified session should not persist to the store")]
    public async Task CommitAsync_Unmodified_ShouldNotWritePayload()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        HttpSessionStoreSession session = new("id", store, IdleTimeout);
        await session.LoadAsync(); // empty, unmodified

        // Act
        await session.CommitAsync();

        // Assert — nothing was written, so the store still has no entry for the id
        (await store.GetAsync("id")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - StoreSession: Concurrent commits are last-commit-wins (no merge)")]
    public async Task CommitAsync_ConcurrentWriters_ShouldBeLastCommitWins()
    {
        // Arrange — two sessions both start from the same empty store snapshot
        InMemoryHttpSessionStore store = new();
        HttpSessionStoreSession a = new("id", store, IdleTimeout);
        HttpSessionStoreSession b = new("id", store, IdleTimeout);
        await a.LoadAsync();
        await b.LoadAsync();

        a.SetString("a", "1");
        b.SetString("b", "2");

        // Act — B commits last, so B's payload replaces A's wholesale
        await a.CommitAsync();
        await b.CommitAsync();

        HttpSessionStoreSession reader = new("id", store, IdleTimeout);
        await reader.LoadAsync();

        // Assert
        reader.GetString("b").ShouldBe("2");
        reader.GetString("a").ShouldBeNull(); // A's key did not survive the later commit
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - StoreSession: Reassigning the id persists state under the new id")]
    public async Task ReassignId_ShouldPersistStateUnderNewId()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        IHttpStoredSession session = store.CreateSession("old-id", IdleTimeout);
        session.SetString("user", "alice");
        await session.CommitAsync(); // stored under old-id

        // Act — regeneration: remove old id, reassign, commit under the new id
        await store.RemoveAsync("old-id");
        session.ReassignId("new-id");
        await session.CommitAsync();

        // Assert
        session.Id.ShouldBe("new-id");
        (await store.GetAsync("old-id")).ShouldBeNull();

        IHttpStoredSession reader = store.CreateSession("new-id", IdleTimeout);
        await reader.LoadAsync();
        reader.GetString("user").ShouldBe("alice");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - StoreSession: Mutating operations should flip IsModified")]
    public void Set_Remove_Clear_ShouldTrackModification()
    {
        // Arrange
        InMemoryHttpSessionStore store = new();
        HttpSessionStoreSession session = new("id", store, IdleTimeout);

        // Act / Assert
        session.IsModified.ShouldBeFalse();

        session.SetString("k", "v");
        session.IsModified.ShouldBeTrue();
    }
}
