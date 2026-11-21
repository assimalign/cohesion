using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// Runs the store contract against <see cref="InMemoryIdentityHubStore"/>, and covers what is
/// that store's own: its clock, its listing order, and the continuation tokens it issues.
/// </summary>
public class InMemoryIdentityHubStoreTests : IdentityHubStoreContractTests
{
    private static readonly IdentityHubRecordSetName _set = "memory.records";

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

    protected override DateTimeOffset Now => _clock.GetUtcNow();

    protected override IIdentityHubStore CreateStore() => InMemoryIdentityHubStore.Create(_clock);

    protected override void Advance(TimeSpan duration) => _clock.Advance(duration);

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null clock")]
    public void Create_WithANullTimeProvider_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => InMemoryIdentityHubStore.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should read the system clock")]
    public async Task Constructor_WithNoClock_ShouldExpireRecordsByTheSystemClock()
    {
        // Arrange
        var store = new InMemoryIdentityHubStore();
        await store.TryAddAsync(_set, Record("past", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await store.TryAddAsync(_set, Record("future", DateTimeOffset.UtcNow.AddHours(1)));

        // Act
        IdentityHubStoredRecord? past = await store.FindAsync(_set, "past");
        IdentityHubStoredRecord? future = await store.FindAsync(_set, "future");

        // Assert
        past.ShouldBeNull();
        future.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should list records in the ordinal order of their keys")]
    public async Task QueryAsync_ForEveryRecord_ShouldListKeysInOrdinalOrder()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        string[] keys = ["delta", "Bravo", "alpha", "charlie", "Alpha", "10", "9"];
        foreach (string key in keys)
        {
            await store.TryAddAsync(_set, Record(key));
        }

        // Act
        IdentityHubRecordPage first = await store.QueryAsync(_set, IdentityHubRecordQuery.All(limit: 4));
        IdentityHubRecordPage second = await store.QueryAsync(_set, IdentityHubRecordQuery.All(limit: 4, first.Continuation));

        // Assert
        string[] listed = first.Records.Concat(second.Records).Select(record => record.Record.Key.Value).ToArray();
        listed.ShouldBe(keys.Order(StringComparer.Ordinal).ToArray());
        first.Records.Count.ShouldBe(4);
        second.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should end the enumeration on the page that holds the last record")]
    public async Task QueryAsync_WhenThePageHoldsTheLastRecord_ShouldCarryNoContinuation()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("a"));
        await store.TryAddAsync(_set, Record("b"));

        // Act
        IdentityHubRecordPage page = await store.QueryAsync(_set, IdentityHubRecordQuery.All(limit: 2));

        // Assert
        page.Records.Count.ShouldBe(2);
        page.Continuation.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should refuse a continuation it did not issue")]
    [InlineData("not-a-token")]
    [InlineData("m1.")]
    [InlineData("m1.%%%")]
    [InlineData("m1._w")]
    [InlineData("m2.YQ")]
    public async Task QueryAsync_WithAContinuationTheStoreDidNotIssue_ShouldThrowContinuationInvalid(string continuation)
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("a"));

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await store.QueryAsync(_set, IdentityHubRecordQuery.All(continuation: continuation)));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreContinuationInvalid);
        exception.Set.ShouldBe(_set);
        exception.Key.ShouldBeNull();
        exception.Message.ShouldNotContain(continuation, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should refuse a foreign continuation for a set that holds nothing")]
    public async Task QueryAsync_WithAForeignContinuationOnAnUnknownSet_ShouldThrowContinuationInvalid()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await store.QueryAsync(_set, IdentityHubRecordQuery.All(continuation: "not-a-token")));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreContinuationInvalid);
        exception.Set.ShouldBe(_set);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should continue after a record that no longer exists")]
    public async Task QueryAsync_WithAContinuationPastTheLastRecord_ShouldReturnAnEmptyLastPage()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("a"));
        IdentityHubWriteResult last = await store.TryAddAsync(_set, Record("b"));
        IdentityHubWriteResult beyond = await store.TryAddAsync(_set, Record("c"));
        IdentityHubRecordPage first = await store.QueryAsync(_set, IdentityHubRecordQuery.All(limit: 2));
        await store.TryRemoveAsync(_set, "b", last.Version);
        await store.TryRemoveAsync(_set, "c", beyond.Version);

        // Act
        IdentityHubRecordPage next = await store.QueryAsync(_set, IdentityHubRecordQuery.All(limit: 2, first.Continuation));

        // Assert
        first.Continuation.ShouldNotBeNull();
        next.Records.ShouldBeEmpty();
        next.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should issue a version no record of the store held before")]
    public async Task TryAddAsync_AcrossRecordSets_ShouldNeverIssueAVersionTwice()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubWriteResult first = await store.TryAddAsync(_set, Record("a"));
        IdentityHubWriteResult second = await store.TryAddAsync("memory.others", Record("a"));
        IdentityHubWriteResult third = await store.TryReplaceAsync(_set, Record("a"), first.Version);

        // Assert
        new[] { first.Version, second.Version, third.Version }.ShouldBeUnique();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should reclaim the records that expired and were never read again")]
    public async Task TryAddAsync_AfterRecordsExpiredUnread_ShouldReclaimThem()
    {
        // Arrange
        InMemoryIdentityHubStore store = InMemoryIdentityHubStore.Create(_clock);
        for (int position = 0; position < 25; position++)
        {
            await store.TryAddAsync(_set, Record($"grant-{position}", Now.AddMinutes(1)));
        }

        _clock.Advance(TimeSpan.FromMinutes(2));
        int heldBeforeTheWrite = store.CountHeld(_set);

        // Act
        IdentityHubWriteResult result = await store.TryAddAsync(_set, Record("permanent"));

        // Assert
        heldBeforeTheWrite.ShouldBe(25);
        result.IsWritten.ShouldBeTrue();
        store.CountHeld(_set).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should reclaim the records that expired and were never read again")]
    public async Task TryReplaceAsync_AfterRecordsExpiredUnread_ShouldReclaimThem()
    {
        // Arrange
        InMemoryIdentityHubStore store = InMemoryIdentityHubStore.Create(_clock);
        IdentityHubWriteResult added = await store.TryAddAsync(_set, Record("permanent"));
        for (int position = 0; position < 10; position++)
        {
            await store.TryAddAsync(_set, Record($"grant-{position}", Now.AddMinutes(1)));
        }

        _clock.Advance(TimeSpan.FromMinutes(2));

        // Act
        IdentityHubWriteResult result = await store.TryReplaceAsync(_set, Record("permanent"), added.Version);

        // Assert
        result.IsWritten.ShouldBeTrue();
        store.CountHeld(_set).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should keep a record whose earlier expiry was replaced or removed")]
    public async Task TryAddAsync_AfterAnExpiryWasReplacedOrRemoved_ShouldKeepTheLiveRecords()
    {
        // Arrange
        InMemoryIdentityHubStore store = InMemoryIdentityHubStore.Create(_clock);
        IdentityHubWriteResult madePermanent = await store.TryAddAsync(_set, Record("made-permanent", Now.AddMinutes(1)));
        IdentityHubWriteResult extended = await store.TryAddAsync(_set, Record("extended", Now.AddMinutes(1)));
        IdentityHubWriteResult readded = await store.TryAddAsync(_set, Record("re-added", Now.AddMinutes(1)));
        await store.TryReplaceAsync(_set, Record("made-permanent"), madePermanent.Version);
        await store.TryReplaceAsync(_set, Record("extended", Now.AddMinutes(10)), extended.Version);
        await store.TryRemoveAsync(_set, "re-added", readded.Version);
        await store.TryAddAsync(_set, Record("re-added"));
        _clock.Advance(TimeSpan.FromMinutes(2));

        // Act
        await store.TryAddAsync(_set, Record("later"));

        // Assert
        (await store.FindAsync(_set, "made-permanent")).ShouldNotBeNull();
        (await store.FindAsync(_set, "extended")).ShouldNotBeNull();
        (await store.FindAsync(_set, "re-added")).ShouldNotBeNull();
        store.CountHeld(_set).ShouldBe(4);
    }

    private static IdentityHubRecord Record(string key, DateTimeOffset? expiresAt = null)
    {
        return new IdentityHubRecord(key, Encoding.UTF8.GetBytes(key), expiresAt);
    }
}
