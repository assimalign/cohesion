using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// Point operations and etag semantics: get/put/delete/exists, insert-only, and
/// compare-and-swap as first-class outcomes.
/// </summary>
public class KeyValueCrudTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Put: Should store an entry and return its etag, readable by Get")]
    public async Task Put_NewKey_ShouldStoreAndReturnETag()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act
        var put = await database.PutAsync(session, Bytes("user:1"), Bytes("ada"), cancellationToken: TestTimeout.Token());
        var entry = await database.GetAsync(session, Bytes("user:1"), TestTimeout.Token());

        // Assert
        put.Applied.ShouldBeTrue();
        put.ETag.ShouldNotBeNull();
        entry.ShouldNotBeNull();
        Text(entry.Value.Value).ShouldBe("ada");
        entry.Value.ETag.ShouldBe(put.ETag.Value);
        Text(entry.Value.Key).ShouldBe("user:1");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Get: Should return null for a missing key")]
    public async Task Get_MissingKey_ShouldReturnNull()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act
        var entry = await database.GetAsync(session, Bytes("missing"), TestTimeout.Token());

        // Assert
        entry.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Put: Should replace an existing entry with a new etag")]
    public async Task Put_ExistingKey_ShouldReplaceWithNewETag()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var first = await database.PutAsync(session, Bytes("k"), Bytes("v1"), cancellationToken: TestTimeout.Token());

        // Act
        var second = await database.PutAsync(session, Bytes("k"), Bytes("v2"), cancellationToken: TestTimeout.Token());
        var entry = await database.GetAsync(session, Bytes("k"), TestTimeout.Token());

        // Assert
        second.Applied.ShouldBeTrue();
        second.ETag.ShouldNotBe(first.ETag);
        Text(entry!.Value.Value).ShouldBe("v2");
        entry.Value.ETag.ShouldBe(second.ETag!.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Delete: Should remove the entry and report whether one was removed")]
    public async Task TryDelete_ExistingThenMissing_ShouldReportEachOutcome()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("k"), Bytes("v"), cancellationToken: TestTimeout.Token());

        // Act / Assert
        (await database.TryDeleteAsync(session, Bytes("k"), cancellationToken: TestTimeout.Token())).ShouldBeTrue();
        (await database.GetAsync(session, Bytes("k"), TestTimeout.Token())).ShouldBeNull();
        (await database.ExistsAsync(session, Bytes("k"), TestTimeout.Token())).ShouldBeFalse();
        (await database.TryDeleteAsync(session, Bytes("k"), cancellationToken: TestTimeout.Token())).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Exists: Should report presence without reading the value")]
    public async Task Exists_PresentKey_ShouldReturnTrue()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("k"), Bytes("v"), cancellationToken: TestTimeout.Token());

        // Act / Assert
        (await database.ExistsAsync(session, Bytes("k"), TestTimeout.Token())).ShouldBeTrue();
        (await database.ExistsAsync(session, Bytes("other"), TestTimeout.Token())).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Put: OnlyIfAbsent should apply once and miss as a first-class outcome after")]
    public async Task Put_OnlyIfAbsent_ShouldMissAgainstExistingKey()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var options = new KeyValuePutOptions { OnlyIfAbsent = true };

        // Act
        var first = await database.PutAsync(session, Bytes("k"), Bytes("v1"), options, TestTimeout.Token());
        var second = await database.PutAsync(session, Bytes("k"), Bytes("v2"), options, TestTimeout.Token());
        var entry = await database.GetAsync(session, Bytes("k"), TestTimeout.Token());

        // Assert: the miss reports the key's CURRENT etag, no exception thrown.
        first.Applied.ShouldBeTrue();
        second.Applied.ShouldBeFalse();
        second.ETag.ShouldBe(first.ETag);
        Text(entry!.Value.Value).ShouldBe("v1");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Put: Compare-and-swap should apply on a matching etag and miss on a stale one")]
    public async Task Put_CompareAndSwap_ShouldApplyOnMatchAndMissOnStale()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var first = await database.PutAsync(session, Bytes("k"), Bytes("v1"), cancellationToken: TestTimeout.Token());

        // Act: swap on the current etag, then retry with the now-stale one.
        var swapped = await database.PutAsync(
            session, Bytes("k"), Bytes("v2"), new KeyValuePutOptions { ExpectedETag = first.ETag }, TestTimeout.Token());
        var stale = await database.PutAsync(
            session, Bytes("k"), Bytes("v3"), new KeyValuePutOptions { ExpectedETag = first.ETag }, TestTimeout.Token());
        var entry = await database.GetAsync(session, Bytes("k"), TestTimeout.Token());

        // Assert
        swapped.Applied.ShouldBeTrue();
        stale.Applied.ShouldBeFalse();
        stale.ETag.ShouldBe(swapped.ETag);
        Text(entry!.Value.Value).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Put: Compare-and-swap against a deleted key should miss with a null current etag")]
    public async Task Put_CompareAndSwapOnDeletedKey_ShouldMissWithNullETag()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var put = await database.PutAsync(session, Bytes("k"), Bytes("v"), cancellationToken: TestTimeout.Token());
        await database.TryDeleteAsync(session, Bytes("k"), cancellationToken: TestTimeout.Token());

        // Act
        var result = await database.PutAsync(
            session, Bytes("k"), Bytes("v2"), new KeyValuePutOptions { ExpectedETag = put.ETag }, TestTimeout.Token());

        // Assert
        result.Applied.ShouldBeFalse();
        result.ETag.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Delete: Compare-and-swap should only delete on a matching etag")]
    public async Task TryDelete_CompareAndSwap_ShouldRequireMatchingETag()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var first = await database.PutAsync(session, Bytes("k"), Bytes("v1"), cancellationToken: TestTimeout.Token());
        var second = await database.PutAsync(session, Bytes("k"), Bytes("v2"), cancellationToken: TestTimeout.Token());

        // Act / Assert: the stale etag misses; the current one deletes.
        (await database.TryDeleteAsync(session, Bytes("k"), first.ETag, TestTimeout.Token())).ShouldBeFalse();
        (await database.ExistsAsync(session, Bytes("k"), TestTimeout.Token())).ShouldBeTrue();
        (await database.TryDeleteAsync(session, Bytes("k"), second.ETag, TestTimeout.Token())).ShouldBeTrue();
        (await database.ExistsAsync(session, Bytes("k"), TestTimeout.Token())).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Put: Contradictory conditions should be rejected at request construction")]
    public void PutRequest_OnlyIfAbsentWithExpectedETag_ShouldThrow()
    {
        // Arrange
        var options = new KeyValuePutOptions { OnlyIfAbsent = true, ExpectedETag = 42 };

        // Act / Assert
        Should.Throw<ArgumentException>(() => new KeyValuePutRequest(Bytes("k"), Bytes("v"), options));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Session: A foreign session should be rejected by the typed surface")]
    public async Task TypedSurface_ForeignSession_ShouldThrow()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        var other = (IKeyValueDatabase)await engine.CreateDatabaseAsync("other");
        await using var foreign = await other.CreateSessionAsync();

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(async () =>
            await database.GetAsync(foreign, Bytes("k"), TestTimeout.Token()));
    }
}
