using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Blob.Client.Internal;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>
/// Serializes the tests that observe the Blob client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(BlobClientEventSourceCollection), DisableParallelization = true)]
public class BlobClientEventSourceCollection
{
}

/// <summary>
/// The Blob client's event source against the repository's EventSource convention: each transfer
/// of each public member starts and stops, or fails, once; a download stops when its last chunk is
/// verified; a listing failure its consumer never saw is reported once by the cleanup; and no blob
/// name or content is written, not even inside the server's failure messages.
/// </summary>
[Collection(nameof(BlobClientEventSourceCollection))]
public sealed class BlobClientEventSourceTests
{
    private const string SecretName = "private/secret-report.bin";

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Client] - BlobClientEventSource: Should be named for its assembly")]
    public void GetName_BlobClientEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(BlobClientEventSource));

        // Assert
        name.ShouldBe(typeof(BlobClientEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Blob.Client");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Client] - BlobClientEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(BlobClientEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Blob.Client", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Client] - BlobClientEventSource: Should report each transfer of each member once, with its declared payload")]
    public async Task Transfers_EachMember_ShouldReportEachOnce()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        byte[] content = new byte[3 * 1024];
        Random.Shared.NextBytes(content);
        using var recorder = new BlobClientEventRecorder();
        BlobClientException failure;

        // Act: upload, download to its end, read the properties, list, delete; then download the
        // deleted blob, which the server refuses.
        await using (BlobConnection connection = await harness.Client.ConnectAsync(timeout.Token))
        {
            (await connection.UploadAsync("files", SecretName, new MemoryStream(content), cancellationToken: timeout.Token)).ShouldBe(content.Length);
            await using (Stream download = await connection.DownloadAsync("files", SecretName, timeout.Token))
            {
                var copy = new MemoryStream();
                await download.CopyToAsync(copy, timeout.Token);
                copy.Length.ShouldBe(content.Length);
            }

            (await connection.GetPropertiesAsync("files", SecretName, timeout.Token)).ShouldNotBeNull();
            int listed = 0;
            await foreach (BlobProperties _ in connection.GetBlobsAsync("files", cancellationToken: timeout.Token))
            {
                listed++;
            }

            listed.ShouldBe(1);
            (await connection.DeleteAsync("files", SecretName, timeout.Token)).ShouldBeTrue();
            failure = await Should.ThrowAsync<BlobClientException>(async () => await connection.DownloadAsync("files", SecretName, timeout.Token));
        }

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "app")).ToArray();
        events.Select(e => (e.EventName, Operation: (string)e.Payload![1]!)).ShouldBe(
        [
            ("TransferStart", "Upload"),
            ("TransferStop", "Upload"),
            ("TransferStart", "Download"),
            ("TransferStop", "Download"),
            ("TransferStart", "GetProperties"),
            ("TransferStop", "GetProperties"),
            ("TransferStart", "List"),
            ("TransferStop", "List"),
            ("TransferStart", "Delete"),
            ("TransferStop", "Delete"),
            ("TransferStart", "Download"),
            ("TransferFailed", "Download"),
        ]);

        var start = events[0];
        start.EventId.ShouldBe(1);
        start.Level.ShouldBe(EventLevel.Verbose);
        (start.Keywords & BlobClientEventSource.Keywords.Transfers).ShouldBe(BlobClientEventSource.Keywords.Transfers);
        start.PayloadNames.ShouldBe(["database", "operation", "container"]);
        start.Payload.ShouldBe(["app", "Upload", "files"]);

        var uploaded = events[1];
        uploaded.EventId.ShouldBe(2);
        uploaded.Level.ShouldBe(EventLevel.Verbose);
        uploaded.PayloadNames.ShouldBe(["database", "operation", "container", "bytes", "durationMilliseconds"]);
        uploaded.Payload![3].ShouldBe((long)content.Length);
        ((double)uploaded.Payload[4]!).ShouldBeGreaterThan(0d);
        events[3].Payload![3].ShouldBe((long)content.Length, "A download stops when its last chunk is verified, with the bytes it received.");
        events[5].Payload![3].ShouldBe(0L);

        // The server's refusal names the blob; the event writes the message without it.
        var failed = events[11];
        failed.EventId.ShouldBe(3);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "operation", "container", "code", "exceptionMessage", "durationMilliseconds"]);
        failed.Payload![3].ShouldBe(failure.Code.ToString());
        failure.Message.ShouldContain(SecretName, Case.Sensitive);
        failed.Payload[4].ShouldBe(failure.Message.Replace(SecretName, BlobClientEventSource.RedactedName, StringComparison.Ordinal));

        // No event carries the blob's name.
        events.SelectMany(e => e.Payload!).OfType<string>()
            .ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Client] - BlobClientEventSource: Should report an upload refused over an existing blob without the blob's name")]
    public async Task UploadAsync_ExistingBlobWithoutOverwrite_ShouldReportFailureWithoutName()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using BlobConnection connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.UploadAsync("files", SecretName, new MemoryStream(new byte[] { 1, 2, 3 }), cancellationToken: timeout.Token);
        using var recorder = new BlobClientEventRecorder();

        // Act
        var failure = await Should.ThrowAsync<BlobClientException>(async () =>
            await connection.UploadAsync("files", SecretName, new MemoryStream(new byte[] { 4, 5, 6 }), overwrite: false, cancellationToken: timeout.Token));

        // Assert
        failure.Message.ShouldContain(SecretName, Case.Sensitive);
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "app")).ToArray();
        events.Select(e => (e.EventName, Operation: (string)e.Payload![1]!)).ShouldBe([("TransferStart", "Upload"), ("TransferFailed", "Upload")]);
        events[1].Payload![4].ShouldBe(failure.Message.Replace(SecretName, BlobClientEventSource.RedactedName, StringComparison.Ordinal));
        events.SelectMany(e => e.Payload!).OfType<string>()
            .ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Client] - BlobClientEventSource: Should report a listing failure its consumer never saw once, from the enumeration's cleanup")]
    public async Task GetBlobsAsync_ConsumerStopsBeforeWorkerFailureSurfaces_ShouldReportListCleanupFailedOnce()
    {
        // Arrange: a peer that sends one listing item and then a frame no listing accepts, so the
        // listing's worker fails while its consumer still holds the first item.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string database = "evt" + Guid.NewGuid().ToString("N");
        await using var listener = new InMemoryConnectionListener();
        await using var client = BlobClient.Create(new BlobClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = database, Principal = "tester", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        var peerClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task peer = ServeMalformedListingAsync(listener, peerClosed, timeout.Token);
        using var recorder = new BlobClientEventRecorder();
        int consumed = 0;

        // Act: take the first item, wait until the worker has failed and closed its broken
        // connection, then stop enumerating, so the cleanup awaits a failed worker, not a canceled one.
        await using (BlobConnection connection = await client.ConnectAsync(timeout.Token))
        {
            await foreach (BlobProperties _ in connection.GetBlobsAsync("files", cancellationToken: timeout.Token))
            {
                consumed++;
                await peerClosed.Task.WaitAsync(timeout.Token);
                break;
            }
        }

        await peer.WaitAsync(timeout.Token);

        // Assert
        consumed.ShouldBe(1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], database)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["TransferStart", "TransferFailed", "ListCleanupFailed"]);
        events[1].Payload![3].ShouldBe(nameof(ProtocolErrorCode.ProtocolViolation));

        var swallowed = events[2];
        swallowed.EventId.ShouldBe(4);
        swallowed.Level.ShouldBe(EventLevel.Verbose);
        swallowed.PayloadNames.ShouldBe(["database", "container", "exceptionType", "exceptionMessage"]);
        swallowed.Payload.ShouldBe([database, "files", typeof(BlobClientException).FullName, "Expected a Blob listing item or completion."]);
    }

    private static async Task ServeMalformedListingAsync(InMemoryConnectionListener listener, TaskCompletionSource closed, CancellationToken token)
    {
        try
        {
            await using var transport = await listener.AcceptAsync(token);
            await using var channel = new ProtocolChannel(transport.AsStream(), BlobProtocol.Family);
            (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Startup);
            await WriteAsync(channel, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, token);
            (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
            await WriteAsync(channel, ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, token);
            (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.List);
            var item = new BlobProperties("item", 1, null, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);
            await WriteAsync(channel, (ProtocolMessageType)BlobProtocolMessageType.Properties, new BlobPropertiesMessage(item).Encode(), token);
            await WriteAsync(channel, (ProtocolMessageType)BlobProtocolMessageType.TransferComplete,
                new BlobTransferCompleteMessage(0).Encode(), token);
            try
            {
                // The client closes the connection its failed listing broke.
                while (await channel.Reader.ReadFrameAsync(token) is not null)
                {
                }
            }
            catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
            {
            }
        }
        finally
        {
            closed.TrySetResult();
        }
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
    {
        var frame = await channel.Reader.ReadFrameAsync(token);
        frame.ShouldNotBeNull();
        return frame.Value;
    }

    private static async ValueTask WriteAsync(ProtocolChannel channel, ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    /// <summary>
    /// Records the events the Blob client's event source writes.
    /// </summary>
    private sealed class BlobClientEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public BlobClientEventRecorder()
        {
            EnableEvents(BlobClientEventSource.Log, EventLevel.Verbose, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, BlobClientEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }
}
