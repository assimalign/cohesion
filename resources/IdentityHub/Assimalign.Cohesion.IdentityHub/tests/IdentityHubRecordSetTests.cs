using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// The typed view hub code holds: a codec's record set in a store, read and written as values.
/// </summary>
public class IdentityHubRecordSetTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(_now);

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null store or codec")]
    public void Create_WithANullStoreOrCodec_ShouldThrowArgumentNullException()
    {
        // Arrange
        var store = new InMemoryIdentityHubStore();
        var codec = new TestGrantCodec();

        // Act
        ArgumentNullException nullStore = Should.Throw<ArgumentNullException>(() => IdentityHubRecordSet<TestGrant>.Create(null!, codec));
        ArgumentNullException nullCodec = Should.Throw<ArgumentNullException>(() => IdentityHubRecordSet<TestGrant>.Create(store, null!));

        // Assert
        nullStore.ParamName.ShouldBe("store");
        nullCodec.ParamName.ShouldBe("codec");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a codec that names no record set")]
    public void Create_WithACodecThatNamesNoRecordSet_ShouldThrowArgumentException()
    {
        // Arrange
        var codec = new StubGrantCodec { Set = default };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityHubRecordSet<TestGrant>.Create(new InMemoryIdentityHubStore(), codec));

        // Assert
        exception.ParamName.ShouldBe("codec");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should take the codec's record set name")]
    public void Create_WithACodec_ShouldExposeItsRecordSetName()
    {
        // Act
        IdentityHubRecordSet<TestGrant> grants = CreateSet();

        // Assert
        grants.Name.Value.ShouldBe("test.grants");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should store a value that FindAsync reads back")]
    public async Task TryAddAsync_ThenFindAsync_ShouldRoundTripTheValue()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        TestGrant grant = Grant("code-1", "ABCD-EFGH");

        // Act
        IdentityHubWriteResult written = await grants.TryAddAsync(grant);
        IdentityHubEntry<TestGrant>? entry = await grants.FindAsync("code-1");

        // Assert
        written.IsWritten.ShouldBeTrue();
        entry.ShouldNotBeNull();
        entry.Value.ShouldBe(grant);
        entry.Key.Value.ShouldBe("code-1");
        entry.Version.ShouldBe(written.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should write the record the codec describes")]
    public async Task TryAddAsync_WithAValue_ShouldStoreTheCodecsRecordInTheCodecsSet()
    {
        // Arrange
        var store = InMemoryIdentityHubStore.Create(_clock);
        IdentityHubRecordSet<TestGrant> grants = CreateSet(store);
        TestGrant grant = Grant("code-1", "ABCD-EFGH");

        // Act
        await grants.TryAddAsync(grant);
        IdentityHubStoredRecord? stored = await store.FindAsync("test.grants", "code-1");

        // Assert
        stored.ShouldNotBeNull();
        stored.Record.ExpiresAt.ShouldBe(grant.ExpiresAt);
        stored.Record.Indexes.ShouldContain(new IdentityHubRecordIndex(TestGrantCodec.UserCode, "ABCD-EFGH", isUnique: true));
        stored.Record.Indexes.ShouldContain(new IdentityHubRecordIndex(TestGrantCodec.Client, "client-1"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should report a refused write as a result")]
    public async Task TryAddAsync_WhenTheStoreRefusesTheWrite_ShouldReturnTheConflict()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));

        // Act
        IdentityHubWriteResult sameKey = await grants.TryAddAsync(Grant("code-1", "WXYZ-1234"));
        IdentityHubWriteResult sameUserCode = await grants.TryAddAsync(Grant("code-2", "ABCD-EFGH"));

        // Assert
        sameKey.Status.ShouldBe(IdentityHubWriteStatus.KeyConflict);
        sameUserCode.Status.ShouldBe(IdentityHubWriteStatus.IndexConflict);
        sameUserCode.ConflictingIndex.ShouldBe(TestGrantCodec.UserCode);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should refuse a null value")]
    public async Task TryAddAsync_WithANullValue_ShouldThrowArgumentNullException()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await grants.TryAddAsync(null!));
        await Should.ThrowAsync<ArgumentNullException>(async () => await grants.TryReplaceAsync(null!, "1"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should fail when the codec encodes a value to no record")]
    public async Task TryAddAsync_WhenTheCodecEncodesToNoRecord_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var store = new RecordingIdentityHubStore();
        var codec = new StubGrantCodec { OnEncode = _ => null };
        IdentityHubRecordSet<TestGrant> grants = IdentityHubRecordSet<TestGrant>.Create(store, codec);

        // Act
        InvalidOperationException add = await Should.ThrowAsync<InvalidOperationException>(
            async () => await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH")));
        InvalidOperationException replace = await Should.ThrowAsync<InvalidOperationException>(
            async () => await grants.TryReplaceAsync(Grant("code-1", "ABCD-EFGH"), "1"));

        // Assert
        add.Message.ShouldContain("test.grants", Case.Sensitive);
        replace.Message.ShouldContain("test.grants", Case.Sensitive);
        store.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should return null when no record holds the key")]
    public async Task FindAsync_WhenNoRecordHoldsTheKey_ShouldReturnNull()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();

        // Act
        IdentityHubEntry<TestGrant>? entry = await grants.FindAsync("missing");

        // Assert
        entry.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should not return a value from its expiry")]
    public async Task FindAsync_AfterTheValueExpired_ShouldReturnNull()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));
        _clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        IdentityHubEntry<TestGrant>? byKey = await grants.FindAsync("code-1");
        IdentityHubEntry<TestGrant>? byUserCode = await grants.FindByIndexAsync(TestGrantCodec.UserCode, "ABCD-EFGH");

        // Assert
        byKey.ShouldBeNull();
        byUserCode.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should report a payload the codec cannot read as a corrupt record")]
    public async Task FindAsync_WhenThePayloadDoesNotDecode_ShouldThrowRecordCorrupt()
    {
        // Arrange
        var store = InMemoryIdentityHubStore.Create(_clock);
        IdentityHubRecordSet<TestGrant> grants = CreateSet(store);
        await store.TryAddAsync("test.grants", new IdentityHubRecord("code-1", Encoding.UTF8.GetBytes("{ unreadable-payload")));

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await grants.FindAsync("code-1"));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        exception.Set.ShouldBe(grants.Name);
        exception.Key.ShouldBe(new IdentityHubRecordKey("code-1"));
        exception.InnerException.ShouldBeOfType<JsonException>();
        exception.Message.ShouldContain("test.grants", Case.Sensitive);
        exception.Message.ShouldContain("code-1", Case.Sensitive);
        exception.Message.ShouldNotContain("unreadable-payload");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should report a record the codec decodes to nothing as corrupt")]
    public async Task FindAsync_WhenTheCodecDecodesToNoValue_ShouldThrowRecordCorrupt()
    {
        // Arrange
        var store = InMemoryIdentityHubStore.Create(_clock);
        IdentityHubRecordSet<TestGrant> grants = CreateSet(store);
        await store.TryAddAsync("test.grants", new IdentityHubRecord("code-1", Encoding.UTF8.GetBytes("null")));

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await grants.FindAsync("code-1"));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        exception.Key.ShouldBe(new IdentityHubRecordKey("code-1"));
        exception.InnerException.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should let a hub exception from the codec through unchanged")]
    public async Task FindAsync_WhenTheCodecThrowsAHubException_ShouldNotTranslateIt()
    {
        // Arrange
        var store = InMemoryIdentityHubStore.Create(_clock);
        var failure = new IdentityHubStoreException(IdentityHubErrorCode.StoreUnavailable, "The key that protects the record is unavailable.");
        var codec = new StubGrantCodec { OnDecode = _ => throw failure };
        IdentityHubRecordSet<TestGrant> grants = IdentityHubRecordSet<TestGrant>.Create(store, codec);
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await grants.FindAsync("code-1"));

        // Assert
        exception.ShouldBeSameAs(failure);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByIndexAsync: Should return the value that lists a unique index value")]
    public async Task FindByIndexAsync_WithAUniqueIndexValue_ShouldReturnTheValue()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));
        IdentityHubWriteResult written = await grants.TryAddAsync(Grant("code-2", "WXYZ-1234"));

        // Act
        IdentityHubEntry<TestGrant>? entry = await grants.FindByIndexAsync(TestGrantCodec.UserCode, "WXYZ-1234");
        IdentityHubEntry<TestGrant>? missing = await grants.FindByIndexAsync(TestGrantCodec.UserCode, "0000-0000");

        // Assert
        entry.ShouldNotBeNull();
        entry.Value.Code.ShouldBe("code-2");
        entry.Version.ShouldBe(written.Version);
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByIndexAsync: Should refuse to pick one of several values")]
    public async Task FindByIndexAsync_WhenSeveralValuesListTheIndexValue_ShouldThrowInvalidOperationException()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));
        await grants.TryAddAsync(Grant("code-2", "WXYZ-1234"));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await grants.FindByIndexAsync(TestGrantCodec.Client, "client-1"));

        // Assert
        exception.Message.ShouldContain("client", Case.Sensitive);
        exception.Message.ShouldContain("test.grants", Case.Sensitive);
        exception.Message.ShouldNotContain("client-1");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByIndexAsync: Should read past short and empty pages")]
    public async Task FindByIndexAsync_WhenTheStoreReturnsShortPages_ShouldReadToTheLastPage()
    {
        // Arrange
        var store = new ShortPageIdentityHubStore(InMemoryIdentityHubStore.Create(_clock));
        IdentityHubRecordSet<TestGrant> grants = CreateSet(store);
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));
        await grants.TryAddAsync(Grant("code-2", "WXYZ-1234", clientId: "client-2"));

        // Act
        IdentityHubEntry<TestGrant>? entry = await grants.FindByIndexAsync(TestGrantCodec.UserCode, "ABCD-EFGH");
        int queries = store.Queries;
        IdentityHubEntry<TestGrant>? missing = await grants.FindByIndexAsync(TestGrantCodec.UserCode, "0000-0000");

        // Assert
        entry.ShouldNotBeNull().Value.Code.ShouldBe("code-1");
        queries.ShouldBe(2);
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByIndexAsync: Should find a second value on a later page")]
    public async Task FindByIndexAsync_WhenTheSecondValueIsOnALaterPage_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var store = new ShortPageIdentityHubStore(InMemoryIdentityHubStore.Create(_clock));
        IdentityHubRecordSet<TestGrant> grants = CreateSet(store);
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));
        await grants.TryAddAsync(Grant("code-2", "WXYZ-1234"));

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await grants.FindByIndexAsync(TestGrantCodec.Client, "client-1"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should decode every entry and carry the continuation")]
    public async Task QueryAsync_AcrossPages_ShouldReturnEveryValueOnce()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        for (int number = 0; number < 5; number++)
        {
            await grants.TryAddAsync(Grant($"code-{number}", $"USER-{number}", clientId: number % 2 == 0 ? "even" : "odd"));
        }

        // Act
        IdentityHubEntryPage<TestGrant> first = await grants.QueryAsync(IdentityHubRecordQuery.All(limit: 3));
        IdentityHubEntryPage<TestGrant> second = await grants.QueryAsync(IdentityHubRecordQuery.All(limit: 3, first.Continuation));
        IdentityHubEntryPage<TestGrant> even = await grants.QueryAsync(IdentityHubRecordQuery.ByIndex(TestGrantCodec.Client, "even"));

        // Assert
        first.Entries.Count.ShouldBe(3);
        first.Continuation.ShouldNotBeNull();
        second.Continuation.ShouldBeNull();
        first.Entries.Concat(second.Entries).Select(entry => entry.Value.Code)
            .ShouldBe(["code-0", "code-1", "code-2", "code-3", "code-4"], ignoreOrder: true);
        even.Entries.Select(entry => entry.Value.Code).ShouldBe(["code-0", "code-2", "code-4"], ignoreOrder: true);
        foreach (IdentityHubEntry<TestGrant> entry in first.Entries)
        {
            entry.Key.Value.ShouldBe(entry.Value.Code);
            entry.Version.Value.ShouldNotBeNull();
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should refuse a null query")]
    public async Task QueryAsync_WithANullQuery_ShouldThrowArgumentNullException()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await grants.QueryAsync(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should fail when the store answers with no page")]
    public async Task QueryAsync_WhenTheStoreReturnsNoPage_ShouldThrowStoreException()
    {
        // Arrange
        var store = new RecordingIdentityHubStore { Page = null };
        IdentityHubRecordSet<TestGrant> grants = CreateSet(store);

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await grants.QueryAsync(IdentityHubRecordQuery.All()));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.Unknown);
        exception.Set.ShouldBe(grants.Name);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should replace the value only at the version that was read")]
    public async Task TryReplaceAsync_WithTheVersionThatWasRead_ShouldReplaceOnce()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH"));
        IdentityHubEntry<TestGrant> pending = (await grants.FindAsync("code-1")).ShouldNotBeNull();

        // Act
        IdentityHubWriteResult approved = await grants.TryReplaceAsync(pending.Value with { Subject = "alice" }, pending.Version);
        IdentityHubWriteResult denied = await grants.TryReplaceAsync(pending.Value with { Subject = "mallory" }, pending.Version);

        // Assert
        approved.IsWritten.ShouldBeTrue();
        denied.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        IdentityHubEntry<TestGrant> current = (await grants.FindAsync("code-1")).ShouldNotBeNull();
        current.Value.Subject.ShouldBe("alice");
        current.Version.ShouldBe(approved.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should let exactly one of two callers redeem a grant")]
    public async Task TryRemoveAsync_WhenTwoCallersRedeemOneGrant_ShouldSucceedForExactlyOne()
    {
        // Arrange
        IdentityHubRecordSet<TestGrant> grants = CreateSet();
        await grants.TryAddAsync(Grant("code-1", "ABCD-EFGH") with { Subject = "alice" });
        IdentityHubEntry<TestGrant> first = (await grants.FindAsync("code-1")).ShouldNotBeNull();
        IdentityHubEntry<TestGrant> second = (await grants.FindAsync("code-1")).ShouldNotBeNull();

        // Act
        bool firstRedeemed = await grants.TryRemoveAsync(first.Key, first.Version);
        bool secondRedeemed = await grants.TryRemoveAsync(second.Key, second.Version);

        // Assert
        firstRedeemed.ShouldBeTrue();
        secondRedeemed.ShouldBeFalse();
        (await grants.FindAsync("code-1")).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Cancellation: Should pass the token to the store")]
    public async Task EveryOperation_WithACancellationToken_ShouldPassItToTheStore()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingIdentityHubStore();
        IdentityHubRecordSet<TestGrant> grants = CreateSet(store);
        TestGrant grant = Grant("code-1", "ABCD-EFGH");

        // Act & Assert
        await grants.FindAsync("code-1", cancellation.Token);
        store.CancellationToken.ShouldBe(cancellation.Token);

        using var second = new CancellationTokenSource();
        await grants.QueryAsync(IdentityHubRecordQuery.All(), second.Token);
        store.CancellationToken.ShouldBe(second.Token);

        using var third = new CancellationTokenSource();
        await grants.TryAddAsync(grant, third.Token);
        store.CancellationToken.ShouldBe(third.Token);

        using var fourth = new CancellationTokenSource();
        await grants.TryReplaceAsync(grant, "1", fourth.Token);
        store.CancellationToken.ShouldBe(fourth.Token);

        using var fifth = new CancellationTokenSource();
        await grants.TryRemoveAsync("code-1", "1", fifth.Token);
        store.CancellationToken.ShouldBe(fifth.Token);

        using var sixth = new CancellationTokenSource();
        await grants.FindByIndexAsync(TestGrantCodec.UserCode, "ABCD-EFGH", sixth.Token);
        store.CancellationToken.ShouldBe(sixth.Token);
        store.Set.ShouldBe(grants.Name);
    }

    private static TestGrant Grant(string code, string userCode, string clientId = "client-1")
    {
        return new TestGrant(code, userCode, clientId, Subject: null, ExpiresAt: _now.AddMinutes(10));
    }

    private IdentityHubRecordSet<TestGrant> CreateSet(IIdentityHubStore? store = null)
    {
        return IdentityHubRecordSet<TestGrant>.Create(store ?? InMemoryIdentityHubStore.Create(_clock), new TestGrantCodec());
    }
}
