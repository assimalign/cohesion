using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>Typed Blob operations and transfer lifetime against the actual in-memory wire server.</summary>
public sealed class BlobClientTests
{
    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Upload: Nonseekable streams round-trip with bounded reads")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadAsync_NonseekableSource_ShouldRoundTripAndRetainSourceOwnership(bool knownLength)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        const long length = 3L * BlobProtocol.MaxChunkLength + 17;
        using var source = new GeneratedContentStream(length);

        (await connection.UploadAsync("files", "images/large.bin", source, "application/test",
            length: knownLength ? length : -1, cancellationToken: timeout.Token)).ShouldBe(length);

        source.WasDisposed.ShouldBeFalse();
        source.BytesRead.ShouldBe(length);
        source.LargestReadRequest.ShouldBeLessThanOrEqualTo(BlobProtocol.MaxChunkLength);
        var properties = await connection.GetPropertiesAsync("files", "images/large.bin", timeout.Token);
        properties.ShouldNotBeNull();
        properties.Value.Name.ShouldBe("images/large.bin");
        properties.Value.Length.ShouldBe(length);
        properties.Value.ContentType.ShouldBe("application/test");
        properties.Value.ETag.ShouldNotBe(0UL);
        await using var download = await connection.DownloadAsync("files", "images/large.bin", timeout.Token);
        download.CanSeek.ShouldBeFalse();
        var buffer = new byte[31_337];
        long read = 0;
        int count;
        while ((count = await download.ReadAsync(buffer, timeout.Token)) != 0)
        {
            for (int index = 0; index < count; index++)
            {
                buffer[index].ShouldBe(GeneratedContentStream.ContentByte(read + index));
            }
            read += count;
        }
        read.ShouldBe(length);
        (await connection.GetPropertiesAsync("files", "images/large.bin", timeout.Token))!.Value.Length.ShouldBe(length);
    }

    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Catalog: Empty objects, ordered prefixes, properties and delete cross the wire")]
    public async Task Catalog_Operations_ShouldPreserveMetadataAndOrdinalPrefixOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        foreach (string name in new[] { "images/z", "elsewhere", "images/a" })
        {
            using var empty = new MemoryStream();
            (await connection.UploadAsync("files", name, empty, cancellationToken: timeout.Token)).ShouldBe(0);
        }

        var names = new List<string>();
        await foreach (var properties in connection.GetBlobsAsync("files", "images/", timeout.Token))
        {
            properties.Length.ShouldBe(0);
            names.Add(properties.Name);
        }
        names.ShouldBe(new[] { "images/a", "images/z" });
        await using (var empty = await connection.DownloadAsync("files", "images/a", timeout.Token))
        {
            (await empty.ReadAsync(new byte[1], timeout.Token)).ShouldBe(0);
        }
        (await connection.DeleteAsync("files", "images/a", timeout.Token)).ShouldBeTrue();
        (await connection.DeleteAsync("files", "images/a", timeout.Token)).ShouldBeFalse();
        (await connection.GetPropertiesAsync("files", "images/a", timeout.Token)).ShouldBeNull();
        await Should.ThrowAsync<BlobClientException>(async () =>
            await connection.DownloadAsync("files", "images/a", timeout.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Upload length: A short declared-length source never publishes partial content")]
    public async Task UploadAsync_SourceShorterThanDeclaredLength_ShouldRollbackPartialContent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        using var source = new GeneratedContentStream(BlobProtocol.MaxChunkLength);

        var exception = await Should.ThrowAsync<BlobClientException>(async () =>
            await connection.UploadAsync("files", "target", source, length: 2L * BlobProtocol.MaxChunkLength, cancellationToken: timeout.Token));

        exception.Message.ShouldContain("declared length", Case.Sensitive);
        await connection.DisposeAsync();
        await WaitForSessionCleanupAsync(harness.Server, timeout.Token);
        await VerifyAbortedWriteAsync(harness.Container, false, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Upload overwrite: Rejection preserves committed bytes and releases the pool")]
    public async Task UploadAsync_OverwriteForbidden_ShouldPreserveCommittedBlobAndAllowNextRental()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using (var connection = await harness.Client.ConnectAsync(timeout.Token))
        {
            using var original = new MemoryStream("committed"u8.ToArray());
            await connection.UploadAsync("files", "target", original, cancellationToken: timeout.Token);
            using var replacement = new GeneratedContentStream(2L * BlobProtocol.MaxChunkLength);
            await Should.ThrowAsync<BlobClientException>(async () =>
                await connection.UploadAsync("files", "target", replacement, overwrite: false, cancellationToken: timeout.Token));
        }

        await using var next = await harness.Client.ConnectAsync(timeout.Token);
        (await next.GetPropertiesAsync("files", "target", timeout.Token))!.Value.Length.ShouldBe(9);
        await VerifyAbortedWriteAsync(harness.Container, true, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Upload cancellation: Accepted partial writes never become visible")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_CanceledAfterAcknowledgedChunk_ShouldAbortNewOrReplacementBlob(bool replaceExisting)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        if (replaceExisting)
        {
            await using var existing = await harness.Container.OpenWriteAsync("target", cancellationToken: timeout.Token);
            await existing.WriteAsync("committed"u8.ToArray(), timeout.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        using var source = new GatedContentStream();
        var upload = connection.UploadAsync("files", "target", source, cancellationToken: cancellation.Token).AsTask();
        await source.FirstChunkAccepted.Task.WaitAsync(timeout.Token);
        source.BytesRead.ShouldBe(BlobProtocol.MaxChunkLength);

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await upload.WaitAsync(timeout.Token));
        await connection.DisposeAsync();
        await WaitForSessionCleanupAsync(harness.Server, timeout.Token);
        await VerifyAbortedWriteAsync(harness.Container, replaceExisting, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Disconnect: Accepted partial writes never become visible")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_DisconnectedAfterAcknowledgedChunk_ShouldAbortNewOrReplacementBlob(bool replaceExisting)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        if (replaceExisting)
        {
            await using var existing = await harness.Container.OpenWriteAsync("target", cancellationToken: timeout.Token);
            await existing.WriteAsync("committed"u8.ToArray(), timeout.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        using var source = new GatedContentStream();
        var upload = connection.UploadAsync("files", "target", source, cancellationToken: timeout.Token).AsTask();
        await source.FirstChunkAccepted.Task.WaitAsync(timeout.Token);

        harness.Factory.LastConnection.ShouldNotBeNull();
        harness.Factory.LastConnection.Abort(new IOException("Injected client disconnect."));
        source.Resume.TrySetResult();

        Exception? failure = await Record.ExceptionAsync(async () => await upload.WaitAsync(timeout.Token));
        failure.ShouldBeOfType<BlobClientException>(failure?.ToString());
        await WaitForSessionCleanupAsync(harness.Server, timeout.Token);
        await VerifyAbortedWriteAsync(harness.Container, replaceExisting, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Download cancellation: Both request and read tokens abort an active stream")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_CanceledAfterFirstChunk_ShouldThrowInsteadOfReturningEof(bool cancelRequestToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        using var source = new GeneratedContentStream(8L * BlobProtocol.MaxChunkLength);
        await connection.UploadAsync("files", "target", source, cancellationToken: timeout.Token);
        await using var download = await connection.DownloadAsync("files", "target", cancelRequestToken ? cancellation.Token : timeout.Token);
        var buffer = new byte[BlobProtocol.MaxChunkLength];
        (await download.ReadAsync(buffer, timeout.Token)).ShouldBeGreaterThan(0);

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            _ = await download.ReadAsync(buffer, cancelRequestToken ? timeout.Token : cancellation.Token);
        });
        await download.DisposeAsync();
        await WaitForSessionCleanupAsync(harness.Server, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Download disposal: Abandoning content releases a single-connection pool")]
    public async Task DownloadAsync_DisposedBeforeEof_ShouldReleasePoolAndAllowFreshExchange()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using (var connection = await harness.Client.ConnectAsync(timeout.Token))
        {
            using var source = new GeneratedContentStream(8L * BlobProtocol.MaxChunkLength);
            await connection.UploadAsync("files", "target", source, cancellationToken: timeout.Token);
            await using var download = await connection.DownloadAsync("files", "target", timeout.Token);
            (await download.ReadAsync(new byte[1], timeout.Token)).ShouldBe(1);
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await connection.GetPropertiesAsync("files", "target", timeout.Token));
        }

        await using var next = await harness.Client.ConnectAsync(timeout.Token);
        (await next.GetPropertiesAsync("files", "target", timeout.Token))!.Value.Length.ShouldBe(8L * BlobProtocol.MaxChunkLength);
    }

    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Server read failure: Disposed storage produces a client error after content")]
    public async Task DownloadAsync_StorageFailsMidTransfer_ShouldExposeServerError()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        using var source = new GeneratedContentStream(16L * BlobProtocol.MaxChunkLength);
        await connection.UploadAsync("files", "target", source, cancellationToken: timeout.Token);
        await using var download = await connection.DownloadAsync("files", "target", timeout.Token);
        var buffer = new byte[BlobProtocol.MaxChunkLength];
        long received = await download.ReadAsync(buffer, timeout.Token);
        received.ShouldBeGreaterThan(0);

        await harness.Database.DisposeAsync();

        var exception = await Should.ThrowAsync<BlobClientException>(async () =>
        {
            int count;
            while ((count = await download.ReadAsync(buffer, timeout.Token)) != 0)
            {
                received += count;
            }
        });
        exception.Message.ShouldNotBeNullOrWhiteSpace();
        exception.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        received.ShouldBeLessThan(16L * BlobProtocol.MaxChunkLength);
    }

    private static async Task WaitForSessionCleanupAsync(BlobDatabaseServer server, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        // Observe autonomous disconnect cleanup; stopping the server here could
        // hide a failed abort behind the server's forced-shutdown cancellation.
        while (server.Context.Sessions.Count != 0)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task VerifyAbortedWriteAsync(IBlobContainer container, bool replaceExisting, CancellationToken token)
    {
        var properties = await container.GetPropertiesAsync("target", token);
        if (replaceExisting)
        {
            properties.ShouldNotBeNull();
            properties.Value.Length.ShouldBe(9);
            await using var committed = await container.OpenReadAsync("target", token);
            var buffer = new byte[9];
            await committed.ReadExactlyAsync(buffer, token);
            buffer.ShouldBe("committed"u8.ToArray());
        }
        else
        {
            properties.ShouldBeNull();
            await Should.ThrowAsync<DatabaseException>(async () => await container.OpenReadAsync("target", token));
            await foreach (var item in container.GetBlobsAsync(cancellationToken: token))
            {
                item.Name.ShouldNotBe("target");
            }
        }
    }
}
