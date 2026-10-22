using Shouldly;
using Xunit;

using Assimalign.Cohesion.Caching.InMemory.Tests.TestObjects;

namespace Assimalign.Cohesion.Caching.InMemory.Tests;

public class MemoryCacheTokenOwnershipTests
{
    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Entry callbacks remain a commit-time snapshot")]
    public void Commit_CallbackListChangesAfterCommit_ShouldKeepOriginalSnapshot()
    {
        // Arrange
        using var cache = new MemoryCache();
        int originalCalls = 0;
        int lateCalls = 0;
        var entry = cache.CreateEntry("key");
        entry.PostEvictionCallbacks.ShouldBeSameAs(entry.PostEvictionCallbacks);
        entry.ExpirationTokens.ShouldBeSameAs(entry.ExpirationTokens);
        entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, _, _) => originalCalls++));
        entry.Dispose();
        entry.PostEvictionCallbacks.Clear();
        entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, _, _) => lateCalls++));

        // Act
        cache.Remove("key");

        // Assert
        originalCalls.ShouldBe(1);
        lateCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Retired token callbacks preserve replacement entries")]
    public void Replace_TokenNotifiesDuringUnsubscription_ShouldKeepReplacement()
    {
        // Arrange
        using var cache = new MemoryCache();
        var token = new CallbackOnDisposeChangeToken();
        using (var entry = cache.CreateEntry("key"))
        {
            entry.Value = "original";
            entry.ExpirationTokens.Add(token);
        }

        // Act
        using (var replacement = cache.CreateEntry("key"))
        {
            replacement.Value = "replacement";
        }

        // Assert
        token.Notifications.ShouldBe(1);
        cache.TryGetValue("key", out var value).ShouldBeTrue();
        value.ShouldBe("replacement");
    }
}
