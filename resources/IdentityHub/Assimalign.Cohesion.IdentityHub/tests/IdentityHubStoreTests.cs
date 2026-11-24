using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// What the <see cref="IdentityHubStore"/> base class does for an implementation: it refuses bad
/// arguments and a canceled token before the implementation runs, and hands valid calls through
/// unchanged.
/// </summary>
public class IdentityHubStoreTests
{
    private static readonly IdentityHubRecordSetName _set = "base.records";
    private static readonly IdentityHubRecordKey _key = "record-1";
    private static readonly IdentityHubRecordVersion _version = "7";

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should refuse default arguments before the store runs")]
    public async Task FindAsync_WithADefaultSetOrKey_ShouldThrowAndNotReachTheStore()
    {
        // Arrange
        var store = new RecordingIdentityHubStore();

        // Act
        ArgumentException set = await Should.ThrowAsync<ArgumentException>(async () => await store.FindAsync(default, _key));
        ArgumentException key = await Should.ThrowAsync<ArgumentException>(async () => await store.FindAsync(_set, default));

        // Assert
        set.ParamName.ShouldBe("set");
        key.ParamName.ShouldBe("key");
        store.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should refuse a default set or null query before the store runs")]
    public async Task QueryAsync_WithADefaultSetOrNullQuery_ShouldThrowAndNotReachTheStore()
    {
        // Arrange
        var store = new RecordingIdentityHubStore();

        // Act
        ArgumentException set = await Should.ThrowAsync<ArgumentException>(
            async () => await store.QueryAsync(default, IdentityHubRecordQuery.All()));
        ArgumentNullException query = await Should.ThrowAsync<ArgumentNullException>(
            async () => await store.QueryAsync(_set, null!));

        // Assert
        set.ParamName.ShouldBe("set");
        query.ParamName.ShouldBe("query");
        store.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should refuse a default set or null record before the store runs")]
    public async Task TryAddAsync_WithADefaultSetOrNullRecord_ShouldThrowAndNotReachTheStore()
    {
        // Arrange
        var store = new RecordingIdentityHubStore();

        // Act
        ArgumentException set = await Should.ThrowAsync<ArgumentException>(async () => await store.TryAddAsync(default, Record()));
        ArgumentNullException record = await Should.ThrowAsync<ArgumentNullException>(async () => await store.TryAddAsync(_set, null!));

        // Assert
        set.ParamName.ShouldBe("set");
        record.ParamName.ShouldBe("record");
        store.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should refuse bad arguments before the store runs")]
    public async Task TryReplaceAsync_WithADefaultSetOrVersionOrNullRecord_ShouldThrowAndNotReachTheStore()
    {
        // Arrange
        var store = new RecordingIdentityHubStore();

        // Act
        ArgumentException set = await Should.ThrowAsync<ArgumentException>(
            async () => await store.TryReplaceAsync(default, Record(), _version));
        ArgumentNullException record = await Should.ThrowAsync<ArgumentNullException>(
            async () => await store.TryReplaceAsync(_set, null!, _version));
        ArgumentException version = await Should.ThrowAsync<ArgumentException>(
            async () => await store.TryReplaceAsync(_set, Record(), default));

        // Assert
        set.ParamName.ShouldBe("set");
        record.ParamName.ShouldBe("record");
        version.ParamName.ShouldBe("expectedVersion");
        store.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should refuse default arguments before the store runs")]
    public async Task TryRemoveAsync_WithADefaultSetKeyOrVersion_ShouldThrowAndNotReachTheStore()
    {
        // Arrange
        var store = new RecordingIdentityHubStore();

        // Act
        ArgumentException set = await Should.ThrowAsync<ArgumentException>(
            async () => await store.TryRemoveAsync(default, _key, _version));
        ArgumentException key = await Should.ThrowAsync<ArgumentException>(
            async () => await store.TryRemoveAsync(_set, default, _version));
        ArgumentException version = await Should.ThrowAsync<ArgumentException>(
            async () => await store.TryRemoveAsync(_set, _key, default));

        // Assert
        set.ParamName.ShouldBe("set");
        key.ParamName.ShouldBe("key");
        version.ParamName.ShouldBe("expectedVersion");
        store.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Cancellation: Should observe a canceled token before the store runs")]
    public async Task EveryOperation_WithACanceledToken_ShouldThrowAndNotReachTheStore()
    {
        // Arrange
        var store = new RecordingIdentityHubStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken canceled = cancellation.Token;

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.FindAsync(_set, _key, canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.QueryAsync(_set, IdentityHubRecordQuery.All(), canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.TryAddAsync(_set, Record(), canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.TryReplaceAsync(_set, Record(), _version, canceled));
        await Should.ThrowAsync<OperationCanceledException>(async () => await store.TryRemoveAsync(_set, _key, _version, canceled));
        store.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should hand valid arguments to the store unchanged")]
    public async Task FindAsync_WithValidArguments_ShouldForwardThemAndReturnTheStoresAnswer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var found = new IdentityHubStoredRecord(Record(), _version);
        var store = new RecordingIdentityHubStore { Found = found };

        // Act
        IdentityHubStoredRecord? result = await store.FindAsync(_set, _key, cancellation.Token);

        // Assert
        result.ShouldBeSameAs(found);
        store.Calls.ShouldBe(["FindCoreAsync"]);
        store.Set.ShouldBe(_set);
        store.Key.ShouldBe(_key);
        store.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - QueryAsync: Should hand valid arguments to the store unchanged")]
    public async Task QueryAsync_WithValidArguments_ShouldForwardThemAndReturnTheStoresAnswer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var page = new IdentityHubRecordPage([new IdentityHubStoredRecord(Record(), _version)], "next");
        var store = new RecordingIdentityHubStore { Page = page };
        IdentityHubRecordQuery query = IdentityHubRecordQuery.All(limit: 5);

        // Act
        IdentityHubRecordPage result = await store.QueryAsync(_set, query, cancellation.Token);

        // Assert
        result.ShouldBeSameAs(page);
        store.Calls.ShouldBe(["QueryCoreAsync"]);
        store.Set.ShouldBe(_set);
        store.Query.ShouldBeSameAs(query);
        store.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should hand valid arguments to the store unchanged")]
    public async Task TryAddAsync_WithValidArguments_ShouldForwardThemAndReturnTheStoresAnswer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingIdentityHubStore { WriteResult = IdentityHubWriteResult.KeyConflict };
        IdentityHubRecord record = Record();

        // Act
        IdentityHubWriteResult result = await store.TryAddAsync(_set, record, cancellation.Token);

        // Assert
        result.ShouldBe(IdentityHubWriteResult.KeyConflict);
        store.Calls.ShouldBe(["TryAddCoreAsync"]);
        store.Set.ShouldBe(_set);
        store.Record.ShouldBeSameAs(record);
        store.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should hand valid arguments to the store unchanged")]
    public async Task TryReplaceAsync_WithValidArguments_ShouldForwardThemAndReturnTheStoresAnswer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingIdentityHubStore { WriteResult = IdentityHubWriteResult.Written("8") };
        IdentityHubRecord record = Record();

        // Act
        IdentityHubWriteResult result = await store.TryReplaceAsync(_set, record, _version, cancellation.Token);

        // Assert
        result.ShouldBe(IdentityHubWriteResult.Written("8"));
        store.Calls.ShouldBe(["TryReplaceCoreAsync"]);
        store.Set.ShouldBe(_set);
        store.Record.ShouldBeSameAs(record);
        store.ExpectedVersion.ShouldBe(_version);
        store.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should hand valid arguments to the store unchanged")]
    public async Task TryRemoveAsync_WithValidArguments_ShouldForwardThemAndReturnTheStoresAnswer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingIdentityHubStore { Removed = false };

        // Act
        bool result = await store.TryRemoveAsync(_set, _key, _version, cancellation.Token);

        // Assert
        result.ShouldBeFalse();
        store.Calls.ShouldBe(["TryRemoveCoreAsync"]);
        store.Set.ShouldBe(_set);
        store.Key.ShouldBe(_key);
        store.ExpectedVersion.ShouldBe(_version);
        store.CancellationToken.ShouldBe(cancellation.Token);
    }

    private static IdentityHubRecord Record()
    {
        return new IdentityHubRecord(_key, Encoding.UTF8.GetBytes("payload"));
    }
}
