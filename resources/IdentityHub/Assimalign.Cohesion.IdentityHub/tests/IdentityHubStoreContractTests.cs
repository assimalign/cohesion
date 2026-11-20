using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// The behavior every <see cref="IIdentityHubStore"/> owes the hub. A store's test class derives
/// from this one and supplies the store and its clock; every test here then runs against it.
/// </summary>
public abstract class IdentityHubStoreContractTests
{
    private const int Callers = 32;

    private static readonly IdentityHubRecordSetName _set = "contract.records";
    private static readonly IdentityHubRecordSetName _otherSet = "contract.others";
    private static readonly IdentityHubRecordIndexName _alias = "alias";
    private static readonly IdentityHubRecordIndexName _owner = "owner";

    /// <summary>
    /// Gets the instant the store under test reads as now.
    /// </summary>
    protected abstract DateTimeOffset Now { get; }

    /// <summary>
    /// Creates an empty store.
    /// </summary>
    protected abstract IIdentityHubStore CreateStore();

    /// <summary>
    /// Moves the clock of the store under test forward.
    /// </summary>
    protected abstract void Advance(TimeSpan duration);

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should return null when no record holds the key")]
    public async Task FindAsync_WhenKeyAbsent_ShouldReturnNull()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubStoredRecord? stored = await store.FindAsync(_set, "missing");

        // Assert
        stored.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should return the record as it was added")]
    public async Task FindAsync_AfterAdd_ShouldReturnTheRecordAndItsVersion()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        DateTimeOffset expiresAt = Now.AddHours(1);
        IdentityHubRecord record = Record("grant-1", "payload-1", expiresAt, [Unique(_alias, "ABCD-EFGH"), Shared(_owner, "client-1")]);
        IdentityHubWriteResult written = await store.TryAddAsync(_set, record);

        // Act
        IdentityHubStoredRecord? stored = await store.FindAsync(_set, "grant-1");

        // Assert
        stored.ShouldNotBeNull();
        stored.Version.ShouldBe(written.Version);
        stored.Record.Key.ShouldBe(record.Key);
        Text(stored).ShouldBe("payload-1");
        stored.Record.ExpiresAt.ShouldBe(expiresAt);
        stored.Record.Indexes.ShouldBe(record.Indexes, ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should compare keys ordinally")]
    public async Task FindAsync_WithKeysThatDifferByCase_ShouldTreatThemAsDistinct()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("Alice", "upper"));
        await store.TryAddAsync(_set, Record("alice", "lower"));

        // Act
        IdentityHubStoredRecord? upper = await store.FindAsync(_set, "Alice");
        IdentityHubStoredRecord? lower = await store.FindAsync(_set, "alice");
        IdentityHubStoredRecord? other = await store.FindAsync(_set, "ALICE");

        // Assert
        Text(upper.ShouldNotBeNull()).ShouldBe("upper");
        Text(lower.ShouldNotBeNull()).ShouldBe("lower");
        other.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should keep record sets apart")]
    public async Task FindAsync_InAnotherRecordSet_ShouldReturnNull()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("shared-key"));

        // Act
        IdentityHubStoredRecord? stored = await store.FindAsync(_otherSet, "shared-key");

        // Assert
        stored.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should return a record until its expiry and not from it")]
    public async Task FindAsync_AtExpiry_ShouldReturnNull()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("short-lived", expiresAt: Now.AddMinutes(10)));

        // Act
        Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        IdentityHubStoredRecord? before = await store.FindAsync(_set, "short-lived");
        Advance(TimeSpan.FromSeconds(1));
        IdentityHubStoredRecord? at = await store.FindAsync(_set, "short-lived");

        // Assert
        before.ShouldNotBeNull();
        at.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should store a record and issue its version")]
    public async Task TryAddAsync_WhenKeyAbsent_ShouldWriteAndIssueAVersion()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubWriteResult result = await store.TryAddAsync(_set, Record("grant-1"));

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.Written);
        result.IsWritten.ShouldBeTrue();
        result.Version.Value.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should return the payload byte for byte")]
    public async Task TryAddAsync_WithBinaryPayload_ShouldReturnExactlyTheBytes()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        byte[] payload = new byte[IdentityHubRecord.MaxPayloadLength];
        for (int position = 0; position < payload.Length; position++)
        {
            payload[position] = (byte)(position % 251);
        }

        // Act
        IdentityHubWriteResult largest = await store.TryAddAsync(_set, new IdentityHubRecord("largest", payload));
        IdentityHubWriteResult empty = await store.TryAddAsync(_set, new IdentityHubRecord("empty", ReadOnlyMemory<byte>.Empty));
        IdentityHubStoredRecord? storedLargest = await store.FindAsync(_set, "largest");
        IdentityHubStoredRecord? storedEmpty = await store.FindAsync(_set, "empty");

        // Assert
        largest.IsWritten.ShouldBeTrue();
        empty.IsWritten.ShouldBeTrue();
        storedLargest.ShouldNotBeNull().Record.Payload.Span.SequenceEqual(payload).ShouldBeTrue();
        storedEmpty.ShouldNotBeNull().Record.Payload.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should keep keys of any script and the longest key")]
    public async Task TryAddAsync_WithUnicodeAndLongestKeys_ShouldFindEachByItsOwnKey()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        string[] keys =
        [
            "ключ",
            "鍵",
            "key with spaces",
            "tenant/user:1?x=y#z",
            "\U0001F511",
            new string('k', IdentityHubRecordKey.MaxLength),
        ];

        // Act
        foreach (string key in keys)
        {
            (await store.TryAddAsync(_set, Record(key, key))).IsWritten.ShouldBeTrue();
        }

        // Assert
        foreach (string key in keys)
        {
            IdentityHubStoredRecord? stored = await store.FindAsync(_set, key);
            Text(stored.ShouldNotBeNull()).ShouldBe(key);
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should refuse a key a live record holds")]
    public async Task TryAddAsync_WhenLiveRecordHoldsTheKey_ShouldReturnKeyConflictAndChangeNothing()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult first = await store.TryAddAsync(_set, Record("grant-1", "first"));

        // Act
        IdentityHubWriteResult second = await store.TryAddAsync(_set, Record("grant-1", "second"));

        // Assert
        second.Status.ShouldBe(IdentityHubWriteStatus.KeyConflict);
        second.IsWritten.ShouldBeFalse();
        second.Version.Value.ShouldBeNull();
        IdentityHubStoredRecord stored = (await store.FindAsync(_set, "grant-1")).ShouldNotBeNull();
        Text(stored).ShouldBe("first");
        stored.Version.ShouldBe(first.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should refuse a unique index value another record lists")]
    public async Task TryAddAsync_WhenUniqueIndexValueIsTaken_ShouldReturnIndexConflictAndStoreNothing()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("grant-1", indexes: [Unique(_alias, "ABCD-EFGH")]));

        // Act
        IdentityHubWriteResult result = await store.TryAddAsync(
            _set,
            Record("grant-2", indexes: [Shared(_owner, "client-1"), Unique(_alias, "ABCD-EFGH")]));

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.IndexConflict);
        result.ConflictingIndex.ShouldBe(_alias);
        result.IsWritten.ShouldBeFalse();
        (await store.FindAsync(_set, "grant-2")).ShouldBeNull();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "client-1"))).ShouldBeEmpty();
        IdentityHubStoredRecord holder = (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "ABCD-EFGH"))).ShouldHaveSingleItem();
        holder.Record.Key.Value.ShouldBe("grant-1");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should scope a unique value to its index and record set")]
    public async Task TryAddAsync_WithTheSameUniqueValueUnderAnotherIndexOrSet_ShouldWrite()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("record-1", indexes: [Unique(_alias, "value")]));

        // Act
        IdentityHubWriteResult otherIndex = await store.TryAddAsync(_set, Record("record-2", indexes: [Unique(_owner, "value")]));
        IdentityHubWriteResult otherSet = await store.TryAddAsync(_otherSet, Record("record-3", indexes: [Unique(_alias, "value")]));
        IdentityHubWriteResult otherCase = await store.TryAddAsync(_set, Record("record-4", indexes: [Unique(_alias, "VALUE")]));

        // Assert
        otherIndex.IsWritten.ShouldBeTrue();
        otherSet.IsWritten.ShouldBeTrue();
        otherCase.IsWritten.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should let any number of records share an entry that is not unique")]
    public async Task TryAddAsync_WithASharedIndexEntry_ShouldWriteEveryRecord()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubWriteResult first = await store.TryAddAsync(_set, Record("session-1", indexes: [Shared(_owner, "alice")]));
        IdentityHubWriteResult second = await store.TryAddAsync(_set, Record("session-2", indexes: [Shared(_owner, "alice")]));

        // Assert
        first.IsWritten.ShouldBeTrue();
        second.IsWritten.ShouldBeTrue();
        List<IdentityHubStoredRecord> sessions = await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "alice"));
        Keys(sessions).ShouldBe(["session-1", "session-2"], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should free the key and unique entries of an expired record")]
    public async Task TryAddAsync_AfterTheHolderExpired_ShouldWrite()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("grant-1", "old", Now.AddMinutes(5), [Unique(_alias, "ABCD-EFGH")]));
        Advance(TimeSpan.FromMinutes(5));

        // Act
        IdentityHubWriteResult sameAlias = await store.TryAddAsync(_set, Record("grant-2", "new", indexes: [Unique(_alias, "ABCD-EFGH")]));
        IdentityHubWriteResult sameKey = await store.TryAddAsync(_set, Record("grant-1", "newer"));

        // Assert
        sameAlias.IsWritten.ShouldBeTrue();
        sameKey.IsWritten.ShouldBeTrue();
        Text((await store.FindAsync(_set, "grant-1")).ShouldNotBeNull()).ShouldBe("newer");
        IdentityHubStoredRecord holder = (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "ABCD-EFGH"))).ShouldHaveSingleItem();
        holder.Record.Key.Value.ShouldBe("grant-2");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should write exactly once when callers race for one key")]
    public async Task TryAddAsync_WithConcurrentCallersForOneKey_ShouldWriteExactlyOnce()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubWriteResult[] results = await RaceAsync(
            caller => store.TryAddAsync(_set, Record("contended", $"caller-{caller}")));

        // Assert
        IdentityHubWriteResult winner = results.Where(result => result.IsWritten).ShouldHaveSingleItem();
        results.Count(result => result.Status == IdentityHubWriteStatus.KeyConflict).ShouldBe(Callers - 1);
        (await store.FindAsync(_set, "contended")).ShouldNotBeNull().Version.ShouldBe(winner.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should write exactly once when callers race for one unique value")]
    public async Task TryAddAsync_WithConcurrentCallersForOneUniqueValue_ShouldWriteExactlyOnce()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubWriteResult[] results = await RaceAsync(
            caller => store.TryAddAsync(_set, Record($"user-{caller}", indexes: [Unique(_alias, "alice")])));

        // Assert
        results.Where(result => result.IsWritten).ShouldHaveSingleItem();
        results.Count(result => result.Status == IdentityHubWriteStatus.IndexConflict).ShouldBe(Callers - 1);
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.All())).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should replace the whole record and issue a new version")]
    public async Task TryReplaceAsync_WithTheCurrentVersion_ShouldReplaceTheWholeRecord()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult added = await store.TryAddAsync(
            _set,
            Record("grant-1", "pending", Now.AddMinutes(5), [Unique(_alias, "ABCD-EFGH"), Shared(_owner, "client-1")]));
        DateTimeOffset expiresAt = Now.AddMinutes(30);

        // Act
        IdentityHubWriteResult replaced = await store.TryReplaceAsync(
            _set,
            Record("grant-1", "approved", expiresAt, [Unique(_alias, "WXYZ-1234")]),
            added.Version);

        // Assert
        replaced.Status.ShouldBe(IdentityHubWriteStatus.Written);
        replaced.Version.ShouldNotBe(added.Version);
        IdentityHubStoredRecord stored = (await store.FindAsync(_set, "grant-1")).ShouldNotBeNull();
        stored.Version.ShouldBe(replaced.Version);
        Text(stored).ShouldBe("approved");
        stored.Record.ExpiresAt.ShouldBe(expiresAt);
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "ABCD-EFGH"))).ShouldBeEmpty();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "client-1"))).ShouldBeEmpty();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "WXYZ-1234"))).ShouldHaveSingleItem();
        (await store.TryAddAsync(_set, Record("grant-2", indexes: [Unique(_alias, "ABCD-EFGH")]))).IsWritten.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should refuse a version that is no longer current")]
    public async Task TryReplaceAsync_WithAStaleVersion_ShouldReturnVersionConflictAndChangeNothing()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult added = await store.TryAddAsync(_set, Record("grant-1", "pending"));
        IdentityHubWriteResult approved = await store.TryReplaceAsync(_set, Record("grant-1", "approved"), added.Version);

        // Act
        IdentityHubWriteResult denied = await store.TryReplaceAsync(_set, Record("grant-1", "denied"), added.Version);

        // Assert
        denied.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        denied.IsWritten.ShouldBeFalse();
        IdentityHubStoredRecord stored = (await store.FindAsync(_set, "grant-1")).ShouldNotBeNull();
        Text(stored).ShouldBe("approved");
        stored.Version.ShouldBe(approved.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should refuse when no live record holds the key")]
    public async Task TryReplaceAsync_WhenNoLiveRecordHoldsTheKey_ShouldReturnVersionConflict()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult expiring = await store.TryAddAsync(_set, Record("expiring", expiresAt: Now.AddMinutes(1)));
        IdentityHubWriteResult removed = await store.TryAddAsync(_set, Record("removed"));
        await store.TryRemoveAsync(_set, "removed", removed.Version);
        Advance(TimeSpan.FromMinutes(1));

        // Act
        IdentityHubWriteResult neverAdded = await store.TryReplaceAsync(_set, Record("never-added"), removed.Version);
        IdentityHubWriteResult afterRemove = await store.TryReplaceAsync(_set, Record("removed"), removed.Version);
        IdentityHubWriteResult afterExpiry = await store.TryReplaceAsync(_set, Record("expiring"), expiring.Version);
        IdentityHubWriteResult unknownSet = await store.TryReplaceAsync(_otherSet, Record("removed"), removed.Version);

        // Assert
        neverAdded.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        afterRemove.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        afterExpiry.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        unknownSet.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        (await store.FindAsync(_set, "never-added")).ShouldBeNull();
        (await store.FindAsync(_set, "removed")).ShouldBeNull();
        (await store.FindAsync(_set, "expiring")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should never match a record added again under the same key")]
    public async Task TryReplaceAsync_WithTheVersionOfARemovedRecord_ShouldNotMatchItsSuccessor()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult first = await store.TryAddAsync(_set, Record("grant-1", "first"));
        await store.TryRemoveAsync(_set, "grant-1", first.Version);
        IdentityHubWriteResult second = await store.TryAddAsync(_set, Record("grant-1", "second"));

        // Act
        IdentityHubWriteResult replaced = await store.TryReplaceAsync(_set, Record("grant-1", "stale"), first.Version);
        bool removed = await store.TryRemoveAsync(_set, "grant-1", first.Version);

        // Assert
        second.Version.ShouldNotBe(first.Version);
        replaced.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        removed.ShouldBeFalse();
        Text((await store.FindAsync(_set, "grant-1")).ShouldNotBeNull()).ShouldBe("second");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should let a record keep its own unique entries")]
    public async Task TryReplaceAsync_KeepingItsOwnUniqueEntry_ShouldWrite()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult added = await store.TryAddAsync(_set, Record("user-1", "v1", indexes: [Unique(_alias, "alice")]));

        // Act
        IdentityHubWriteResult replaced = await store.TryReplaceAsync(
            _set,
            Record("user-1", "v2", indexes: [Unique(_alias, "alice"), Unique(_alias, "alice@example.com")]),
            added.Version);

        // Assert
        replaced.IsWritten.ShouldBeTrue();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "alice"))).ShouldHaveSingleItem();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "alice@example.com"))).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should refuse a unique value another record lists")]
    public async Task TryReplaceAsync_ToAUniqueValueAnotherRecordLists_ShouldReturnIndexConflictAndChangeNothing()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("user-1", indexes: [Unique(_alias, "alice")]));
        IdentityHubWriteResult added = await store.TryAddAsync(_set, Record("user-2", "before", indexes: [Unique(_alias, "bob")]));

        // Act
        IdentityHubWriteResult result = await store.TryReplaceAsync(
            _set,
            Record("user-2", "after", indexes: [Unique(_alias, "alice")]),
            added.Version);

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.IndexConflict);
        result.ConflictingIndex.ShouldBe(_alias);
        IdentityHubStoredRecord stored = (await store.FindAsync(_set, "user-2")).ShouldNotBeNull();
        Text(stored).ShouldBe("before");
        stored.Version.ShouldBe(added.Version);
        IdentityHubStoredRecord bob = (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "bob"))).ShouldHaveSingleItem();
        bob.Record.Key.Value.ShouldBe("user-2");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should write exactly once when callers race on one version")]
    public async Task TryReplaceAsync_WithConcurrentCallersForOneVersion_ShouldWriteExactlyOnce()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult added = await store.TryAddAsync(_set, Record("grant-1", "pending"));

        // Act
        IdentityHubWriteResult[] results = await RaceAsync(
            caller => store.TryReplaceAsync(_set, Record("grant-1", $"caller-{caller}"), added.Version));

        // Assert
        IdentityHubWriteResult winner = results.Where(result => result.IsWritten).ShouldHaveSingleItem();
        results.Count(result => result.Status == IdentityHubWriteStatus.VersionConflict).ShouldBe(Callers - 1);
        (await store.FindAsync(_set, "grant-1")).ShouldNotBeNull().Version.ShouldBe(winner.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should remove the record and free its entries")]
    public async Task TryRemoveAsync_WithTheCurrentVersion_ShouldRemoveTheRecordAndItsEntries()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult added = await store.TryAddAsync(
            _set,
            Record("grant-1", indexes: [Unique(_alias, "ABCD-EFGH"), Shared(_owner, "client-1")]));

        // Act
        bool removed = await store.TryRemoveAsync(_set, "grant-1", added.Version);

        // Assert
        removed.ShouldBeTrue();
        (await store.FindAsync(_set, "grant-1")).ShouldBeNull();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.All())).ShouldBeEmpty();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "ABCD-EFGH"))).ShouldBeEmpty();
        (await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "client-1"))).ShouldBeEmpty();
        (await store.TryAddAsync(_set, Record("grant-2", indexes: [Unique(_alias, "ABCD-EFGH")]))).IsWritten.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should refuse a version that is no longer current")]
    public async Task TryRemoveAsync_WithAStaleVersion_ShouldReturnFalseAndKeepTheRecord()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult added = await store.TryAddAsync(_set, Record("grant-1", "pending"));
        await store.TryReplaceAsync(_set, Record("grant-1", "approved"), added.Version);

        // Act
        bool removed = await store.TryRemoveAsync(_set, "grant-1", added.Version);

        // Assert
        removed.ShouldBeFalse();
        Text((await store.FindAsync(_set, "grant-1")).ShouldNotBeNull()).ShouldBe("approved");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should return false when no live record holds the key")]
    public async Task TryRemoveAsync_WhenNoLiveRecordHoldsTheKey_ShouldReturnFalse()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult expiring = await store.TryAddAsync(_set, Record("expiring", expiresAt: Now.AddMinutes(1)));
        IdentityHubWriteResult removed = await store.TryAddAsync(_set, Record("removed"));
        await store.TryRemoveAsync(_set, "removed", removed.Version);
        Advance(TimeSpan.FromMinutes(1));

        // Act
        bool neverAdded = await store.TryRemoveAsync(_set, "never-added", removed.Version);
        bool again = await store.TryRemoveAsync(_set, "removed", removed.Version);
        bool afterExpiry = await store.TryRemoveAsync(_set, "expiring", expiring.Version);
        bool unknownSet = await store.TryRemoveAsync(_otherSet, "removed", removed.Version);

        // Assert
        neverAdded.ShouldBeFalse();
        again.ShouldBeFalse();
        afterExpiry.ShouldBeFalse();
        unknownSet.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should answer true to exactly one of the callers that name one version")]
    public async Task TryRemoveAsync_WithConcurrentCallersForOneVersion_ShouldRemoveExactlyOnce()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubWriteResult added = await store.TryAddAsync(_set, Record("device-code-digest", "approved"));

        // Act
        bool[] results = await RaceAsync(_ => store.TryRemoveAsync(_set, "device-code-digest", added.Version));

        // Assert
        results.Count(removed => removed).ShouldBe(1);
        (await store.FindAsync(_set, "device-code-digest")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should return a last page with no record for an empty set")]
    public async Task QueryAsync_OnAnEmptyRecordSet_ShouldReturnAnEmptyLastPage()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();

        // Act
        IdentityHubRecordPage all = await store.QueryAsync(_set, IdentityHubRecordQuery.All());
        IdentityHubRecordPage byIndex = await store.QueryAsync(_set, IdentityHubRecordQuery.ByIndex(_owner, "nobody"));

        // Assert
        all.ShouldNotBeNull();
        all.Records.ShouldBeEmpty();
        all.Continuation.ShouldBeNull();
        byIndex.ShouldNotBeNull();
        byIndex.Records.ShouldBeEmpty();
        byIndex.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should return every record exactly once across pages")]
    public async Task QueryAsync_ForEveryRecordAcrossPages_ShouldReturnEachRecordExactlyOnce()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        string[] keys = Enumerable.Range(0, 7).Select(number => $"record-{number}").ToArray();
        foreach (string key in keys)
        {
            await store.TryAddAsync(_set, Record(key, key));
        }

        await store.TryAddAsync(_otherSet, Record("elsewhere"));

        // Act
        IdentityHubRecordPage first = await store.QueryAsync(_set, IdentityHubRecordQuery.All(limit: 3));
        List<IdentityHubStoredRecord> records = await ReadAllAsync(store, _set, IdentityHubRecordQuery.All(limit: 3));

        // Assert
        first.Records.Count.ShouldBeLessThanOrEqualTo(3);
        first.Continuation.ShouldNotBeNull();
        Keys(records).ShouldBe(keys, ignoreOrder: true);
        foreach (IdentityHubStoredRecord record in records)
        {
            Text(record).ShouldBe(record.Record.Key.Value);
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should return only the records that list the index value")]
    public async Task QueryAsync_ByIndexAcrossPages_ShouldReturnOnlyAndAllMatchingRecords()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        for (int number = 0; number < 5; number++)
        {
            await store.TryAddAsync(_set, Record($"alice-{number}", indexes: [Shared(_owner, "alice"), Unique(_alias, $"a-{number}")]));
            await store.TryAddAsync(_set, Record($"bob-{number}", indexes: [Shared(_owner, "bob")]));
        }

        await store.TryAddAsync(_set, Record("multi", indexes: [Shared(_owner, "carol"), Shared(_owner, "alice")]));
        await store.TryAddAsync(_set, Record("wrong-index", indexes: [Shared(_alias, "alice")]));
        await store.TryAddAsync(_otherSet, Record("wrong-set", indexes: [Shared(_owner, "alice")]));

        // Act
        List<IdentityHubStoredRecord> alices = await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "alice", limit: 2));
        List<IdentityHubStoredRecord> carols = await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "carol"));
        List<IdentityHubStoredRecord> unique = await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_alias, "a-3"));
        List<IdentityHubStoredRecord> otherCase = await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "Alice"));

        // Assert
        Keys(alices).ShouldBe(["alice-0", "alice-1", "alice-2", "alice-3", "alice-4", "multi"], ignoreOrder: true);
        Keys(carols).ShouldBe(["multi"]);
        Keys(unique).ShouldBe(["alice-3"]);
        otherCase.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should not list a record from its expiry")]
    public async Task QueryAsync_WithExpiredRecords_ShouldListOnlyLiveRecords()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        await store.TryAddAsync(_set, Record("expired-1", expiresAt: Now.AddMinutes(1), indexes: [Shared(_owner, "alice")]));
        await store.TryAddAsync(_set, Record("live-1", expiresAt: Now.AddMinutes(30), indexes: [Shared(_owner, "alice")]));
        await store.TryAddAsync(_set, Record("expired-2", expiresAt: Now.AddMinutes(2), indexes: [Shared(_owner, "alice")]));
        await store.TryAddAsync(_set, Record("live-2", indexes: [Shared(_owner, "alice")]));
        Advance(TimeSpan.FromMinutes(2));

        // Act
        List<IdentityHubStoredRecord> all = await ReadAllAsync(store, _set, IdentityHubRecordQuery.All(limit: 1));
        List<IdentityHubStoredRecord> byIndex = await ReadAllAsync(store, _set, IdentityHubRecordQuery.ByIndex(_owner, "alice", limit: 1));

        // Assert
        Keys(all).ShouldBe(["live-1", "live-2"], ignoreOrder: true);
        Keys(byIndex).ShouldBe(["live-1", "live-2"], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should keep enumerating when records are removed between pages")]
    public async Task QueryAsync_WhenRecordsAreRemovedBetweenPages_ShouldReturnEveryLastingRecordExactlyOnce()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        var versions = new Dictionary<string, IdentityHubRecordVersion>(StringComparer.Ordinal);
        for (int number = 0; number < 9; number++)
        {
            string key = $"record-{number}";
            versions[key] = (await store.TryAddAsync(_set, Record(key))).Version;
        }

        IdentityHubRecordQuery query = IdentityHubRecordQuery.All(limit: 3);
        IdentityHubRecordPage first = await store.QueryAsync(_set, query);
        var seen = new List<string>(Keys(first.Records));

        // Act: remove every record of the first page, and one the enumeration has not reached.
        foreach (string key in seen)
        {
            (await store.TryRemoveAsync(_set, key, versions[key])).ShouldBeTrue();
        }

        string unseen = versions.Keys.First(key => !seen.Contains(key));
        (await store.TryRemoveAsync(_set, unseen, versions[unseen])).ShouldBeTrue();
        first.Continuation.ShouldNotBeNull();
        seen.AddRange(Keys(await ReadAllAsync(store, _set, query.After(first.Continuation))));

        // Assert
        seen.ShouldBeUnique();
        foreach (string key in versions.Keys.Where(key => key != unseen))
        {
            seen.ShouldContain(key);
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Arguments: Should refuse a default record set name, key, or version")]
    public async Task EveryOperation_WithADefaultSetKeyOrVersion_ShouldThrowArgumentException()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubRecordVersion version = (await store.TryAddAsync(_set, Record("grant-1"))).Version;

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(async () => await store.FindAsync(default, "grant-1"));
        await Should.ThrowAsync<ArgumentException>(async () => await store.FindAsync(_set, default));
        await Should.ThrowAsync<ArgumentException>(async () => await store.QueryAsync(default, IdentityHubRecordQuery.All()));
        await Should.ThrowAsync<ArgumentException>(async () => await store.TryAddAsync(default, Record("grant-2")));
        await Should.ThrowAsync<ArgumentException>(async () => await store.TryReplaceAsync(default, Record("grant-1"), version));
        await Should.ThrowAsync<ArgumentException>(async () => await store.TryReplaceAsync(_set, Record("grant-1"), default));
        await Should.ThrowAsync<ArgumentException>(async () => await store.TryRemoveAsync(default, "grant-1", version));
        await Should.ThrowAsync<ArgumentException>(async () => await store.TryRemoveAsync(_set, default, version));
        await Should.ThrowAsync<ArgumentException>(async () => await store.TryRemoveAsync(_set, "grant-1", default));
        (await store.FindAsync(_set, "grant-1")).ShouldNotBeNull().Version.ShouldBe(version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Arguments: Should refuse a null record or query")]
    public async Task EveryOperation_WithANullRecordOrQuery_ShouldThrowArgumentNullException()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubRecordVersion version = (await store.TryAddAsync(_set, Record("grant-1"))).Version;

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await store.QueryAsync(_set, null!));
        await Should.ThrowAsync<ArgumentNullException>(async () => await store.TryAddAsync(_set, null!));
        await Should.ThrowAsync<ArgumentNullException>(async () => await store.TryReplaceAsync(_set, null!, version));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Cancellation: Should observe a canceled token and change nothing")]
    public async Task EveryOperation_WithACanceledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        IIdentityHubStore store = CreateStore();
        IdentityHubRecordVersion version = (await store.TryAddAsync(_set, Record("grant-1", "kept"))).Version;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken canceled = cancellation.Token;

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.FindAsync(_set, "grant-1", canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.QueryAsync(_set, IdentityHubRecordQuery.All(), canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.TryAddAsync(_set, Record("grant-2"), canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.TryReplaceAsync(_set, Record("grant-1", "changed"), version, canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.TryRemoveAsync(_set, "grant-1", version, canceled));
        IdentityHubStoredRecord stored = (await store.FindAsync(_set, "grant-1")).ShouldNotBeNull();
        Text(stored).ShouldBe("kept");
        stored.Version.ShouldBe(version);
        (await store.FindAsync(_set, "grant-2")).ShouldBeNull();
    }

    private static IdentityHubRecord Record(
        string key,
        string payload = "payload",
        DateTimeOffset? expiresAt = null,
        IdentityHubRecordIndex[]? indexes = null)
    {
        return new IdentityHubRecord(key, Encoding.UTF8.GetBytes(payload), expiresAt, indexes);
    }

    private static IdentityHubRecordIndex Unique(IdentityHubRecordIndexName name, string value)
    {
        return new IdentityHubRecordIndex(name, value, isUnique: true);
    }

    private static IdentityHubRecordIndex Shared(IdentityHubRecordIndexName name, string value)
    {
        return new IdentityHubRecordIndex(name, value);
    }

    private static string Text(IdentityHubStoredRecord stored)
    {
        return Encoding.UTF8.GetString(stored.Record.Payload.Span);
    }

    private static string[] Keys(IEnumerable<IdentityHubStoredRecord> records)
    {
        return records.Select(record => record.Record.Key.Value).ToArray();
    }

    // Follows continuations to the last page. A page may be short or empty and still not be the
    // last, so only a null continuation ends the loop; the page count is bounded so that a store
    // that never ends an enumeration fails the test instead of hanging it.
    private static async Task<List<IdentityHubStoredRecord>> ReadAllAsync(
        IIdentityHubStore store,
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var records = new List<IdentityHubStoredRecord>();
        for (int pages = 0; pages < 1000; pages++)
        {
            IdentityHubRecordPage page = await store.QueryAsync(set, query, timeout.Token);
            page.ShouldNotBeNull();
            page.Records.Count.ShouldBeLessThanOrEqualTo(query.Limit);
            records.AddRange(page.Records);
            if (page.Continuation is null)
            {
                return records;
            }

            query = query.After(page.Continuation);
        }

        throw new InvalidOperationException("The store did not end the enumeration within 1000 pages.");
    }

    // Starts every caller behind one gate and opens it once, so that the calls overlap.
    private static async Task<TResult[]> RaceAsync<TResult>(Func<int, ValueTask<TResult>> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TResult>[] callers = Enumerable.Range(0, Callers)
            .Select(caller => Task.Run(async () =>
            {
                await gate.Task;
                return await operation(caller);
            }))
            .ToArray();

        gate.SetResult();
        return await Task.WhenAll(callers);
    }
}
