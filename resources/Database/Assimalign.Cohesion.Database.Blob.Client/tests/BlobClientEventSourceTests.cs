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

using Assimalign.Cohesion.Database.Blob.Client.Internal;

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
/// verified; and no blob name or content is written.
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

        var failed = events[11];
        failed.EventId.ShouldBe(3);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "operation", "container", "code", "exceptionMessage", "durationMilliseconds"]);
        failed.Payload![3].ShouldBe(failure.Code.ToString());
        failed.Payload[4].ShouldBe(failure.Message);

        // No start or stop carries the blob's name.
        events.Where(e => e.EventName != "TransferFailed").SelectMany(e => e.Payload!).OfType<string>()
            .ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Client] - BlobClientEventSource: Should write a listing's swallowed cleanup failure with its declared payload")]
    public async Task ListCleanupFailed_DeclaredPayload_ShouldBeWritten()
    {
        // Arrange: the cleanup swallows only a failure its consumer never saw, which a test cannot
        // provoke deterministically, so the event is written directly for its shape;
        // BlobConnection.GetBlobsAsync writes it from the catch that swallows the failure.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using BlobConnection connection = await harness.Client.ConnectAsync(timeout.Token);
        using var recorder = new BlobClientEventRecorder();

        // Act
        BlobClientEventSource.Log.ListCleanupFailed(connection, "files", new IOException("the listing broke"));

        // Assert
        var swallowed = recorder.Events.Where(e => Equals(e.Payload?[0], "app")).ShouldHaveSingleItem();
        swallowed.EventName.ShouldBe("ListCleanupFailed");
        swallowed.EventId.ShouldBe(4);
        swallowed.Level.ShouldBe(EventLevel.Verbose);
        swallowed.PayloadNames.ShouldBe(["database", "container", "exceptionType", "exceptionMessage"]);
        swallowed.Payload.ShouldBe(["app", "files", typeof(IOException).FullName, "the listing broke"]);
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
