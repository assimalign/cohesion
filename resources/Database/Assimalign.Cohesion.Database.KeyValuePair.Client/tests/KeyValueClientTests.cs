using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// End-to-end acceptance for the typed key-value client (#207): the full typed
/// surface over a live <c>KeyValueDatabaseServer</c> and engine on the in-memory
/// Connections driver — CRUD, etag compare-and-swap as first-class outcomes,
/// ordered/bounded scans, pooling, telemetry, and the error surface.
/// </summary>
public class KeyValueClientTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - CRUD: put/get/exists/delete round-trip with etags")]
    public async Task Crud_PutGetExistsDelete_ShouldRoundTrip()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());

        // Act / Assert: put returns the new etag; get reads it back.
        long etag = await connection.PutAsync(Bytes("user:1"), Bytes("ada"), KeyValueClientTestHarness.Timeout());

        KeyValueClientEntry? entry = await connection.GetAsync(Bytes("user:1"), KeyValueClientTestHarness.Timeout());
        entry.ShouldNotBeNull();
        Text(entry.Value.Key).ShouldBe("user:1");
        Text(entry.Value.Value).ShouldBe("ada");
        entry.Value.ETag.ShouldBe(etag);

        (await connection.ExistsAsync(Bytes("user:1"), KeyValueClientTestHarness.Timeout())).ShouldBeTrue();
        (await connection.TryDeleteAsync(Bytes("user:1"), KeyValueClientTestHarness.Timeout())).ShouldBeTrue();
        (await connection.GetAsync(Bytes("user:1"), KeyValueClientTestHarness.Timeout())).ShouldBeNull();
        (await connection.TryDeleteAsync(Bytes("user:1"), KeyValueClientTestHarness.Timeout())).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - CAS: conditional puts apply on match and miss as first-class outcomes")]
    public async Task Put_WithConditions_ShouldApplyOrMissFirstClass()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());

        // Act: insert-only applies once, then misses with the current etag.
        KeyValueWriteResult first = await connection.PutAsync(
            Bytes("k"), Bytes("v1"), KeyValueWriteCondition.IfAbsent, KeyValueClientTestHarness.Timeout());
        KeyValueWriteResult absentMiss = await connection.PutAsync(
            Bytes("k"), Bytes("v2"), KeyValueWriteCondition.IfAbsent, KeyValueClientTestHarness.Timeout());

        // Assert
        first.Applied.ShouldBeTrue();
        first.ETag.ShouldNotBeNull();
        absentMiss.Applied.ShouldBeFalse();
        absentMiss.ETag.ShouldBe(first.ETag);

        // Act: compare-and-swap on the current etag applies; a stale one misses.
        KeyValueWriteResult swap = await connection.PutAsync(
            Bytes("k"), Bytes("v2"), KeyValueWriteCondition.IfETagMatches(first.ETag!.Value), KeyValueClientTestHarness.Timeout());
        KeyValueWriteResult stale = await connection.PutAsync(
            Bytes("k"), Bytes("v3"), KeyValueWriteCondition.IfETagMatches(first.ETag!.Value), KeyValueClientTestHarness.Timeout());

        // Assert
        swap.Applied.ShouldBeTrue();
        stale.Applied.ShouldBeFalse();
        stale.ETag.ShouldBe(swap.ETag);
        Text((await connection.GetAsync(Bytes("k"), KeyValueClientTestHarness.Timeout()))!.Value.Value).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - CAS: conditional delete requires the matching etag")]
    public async Task TryDelete_WithETag_ShouldRequireMatch()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        long first = await connection.PutAsync(Bytes("k"), Bytes("v1"), KeyValueClientTestHarness.Timeout());
        long second = await connection.PutAsync(Bytes("k"), Bytes("v2"), KeyValueClientTestHarness.Timeout());

        // Act / Assert
        (await connection.TryDeleteAsync(Bytes("k"), first, KeyValueClientTestHarness.Timeout())).ShouldBeFalse();
        (await connection.ExistsAsync(Bytes("k"), KeyValueClientTestHarness.Timeout())).ShouldBeTrue();
        (await connection.TryDeleteAsync(Bytes("k"), second, KeyValueClientTestHarness.Timeout())).ShouldBeTrue();
        (await connection.ExistsAsync(Bytes("k"), KeyValueClientTestHarness.Timeout())).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Scan: ordered, prefixed, and bounded scans materialize in key order")]
    public async Task Scan_WithRanges_ShouldReturnOrderedEntries()
    {
        // Arrange: inserted out of order.
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());

        foreach (string key in new[] { "b:2", "a:1", "b:1", "c:1", "a:2" })
        {
            await connection.PutAsync(Bytes(key), Bytes("v-" + key), KeyValueClientTestHarness.Timeout());
        }

        // Act / Assert: unbounded scan is ascending.
        IReadOnlyList<KeyValueClientEntry> all = await connection.ScanAsync(cancellationToken: KeyValueClientTestHarness.Timeout());
        all.Count.ShouldBe(5);
        Text(all[0].Key).ShouldBe("a:1");
        Text(all[4].Key).ShouldBe("c:1");

        // Prefix scan covers exactly the prefixed keys.
        IReadOnlyList<KeyValueClientEntry> prefixed = await connection.ScanAsync(
            new KeyValueScanRange { Prefix = Bytes("b:") }, KeyValueClientTestHarness.Timeout());
        prefixed.Count.ShouldBe(2);
        Text(prefixed[0].Key).ShouldBe("b:1");
        Text(prefixed[1].Key).ShouldBe("b:2");

        // [start, end) with a limit truncates in order.
        IReadOnlyList<KeyValueClientEntry> bounded = await connection.ScanAsync(
            new KeyValueScanRange { Start = Bytes("a:2"), End = Bytes("c:1"), Limit = 2 }, KeyValueClientTestHarness.Timeout());
        bounded.Count.ShouldBe(2);
        Text(bounded[0].Key).ShouldBe("a:2");
        Text(bounded[1].Key).ShouldBe("b:1");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Pooling: a returned connection's session is reused by the next rent")]
    public async Task ConnectAsync_AfterReturn_ShouldReuseSession()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();

        await using (var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout()))
        {
            await connection.PutAsync(Bytes("k"), Bytes("v"), KeyValueClientTestHarness.Timeout());
        }

        // Act: the pool should hand back the same authenticated session.
        await using (var reused = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout()))
        {
            Text((await reused.GetAsync(Bytes("k"), KeyValueClientTestHarness.Timeout()))!.Value.Value).ShouldBe("v");
        }

        harness.Server.Context.Sessions.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Telemetry: the observer sees command text, counts, and failures")]
    public async Task Observer_AroundCommands_ShouldRecordOutcomes()
    {
        // Arrange
        var observer = new RecordingObserver();
        await using var harness = await KeyValueClientTestHarness.StartAsync(observer: observer);
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());

        // Act
        await connection.PutAsync(Bytes("k"), Bytes("v"), KeyValueClientTestHarness.Timeout());
        await connection.GetAsync(Bytes("k"), KeyValueClientTestHarness.Timeout());
        await connection.TryDeleteAsync(Bytes("k"), KeyValueClientTestHarness.Timeout());

        // Assert: grammar text only — no key or value bytes reach the observer.
        observer.Executing.ShouldBe(["PUT @k @v", "GET @k", "DELETE @k"]);
        observer.Executed.Count.ShouldBe(3);
        observer.Executed[0].AffectedCount.ShouldBe(1); // put applied
        observer.Executed[1].RowCount.ShouldBe(1);      // get returned the entry
        observer.Executed[2].AffectedCount.ShouldBe(1); // delete removed it
        observer.Failed.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Errors: an authentication rejection maps to AuthenticationFailure")]
    public async Task ConnectAsync_WithRejectingAuthenticator_ShouldMapAuthenticationFailure()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync(
            configureServer: options => options.Authenticator = new RejectingAuthenticator());

        // Act
        var failure = await Should.ThrowAsync<KeyValueClientException>(async () =>
            await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(KeyValueClientErrorKind.AuthenticationFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.AuthenticationFailed);
        failure.ConnectionUsable.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Errors: a client-side range violation never reaches the wire")]
    public async Task Scan_PrefixWithBounds_ShouldThrowArgumentException()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());

        // Act / Assert
        await Should.ThrowAsync<ArgumentException>(async () =>
            await connection.ScanAsync(
                new KeyValueScanRange { Prefix = Bytes("p"), Start = Bytes("a") },
                KeyValueClientTestHarness.Timeout()));
    }

    private sealed class RejectingAuthenticator : IDatabaseAuthenticator
    {
        public ValueTask<bool> AuthenticateAsync(string database, string principal, ReadOnlyMemory<byte> evidence, System.Threading.CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
