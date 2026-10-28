using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

[Collection(nameof(DocumentClientEventSourceCollection))]
public sealed class DocumentResponseStreamingTests
{
    private readonly ITestOutputHelper _output;

    public DocumentResponseStreamingTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Put preflights a reply that is one byte too large before committing")]
    public async Task PutAsync_ResponseExceedsFrame_ShouldRefuseBeforeMutation()
    {
        // Arrange: the request fits exactly; the stored reply needs one additional byte.
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("a", harness.Token);
        byte[] content = Encoding.UTF8.GetBytes("\"" + new string('x', (int)ProtocolFrameHeader.MaxPayloadLength - 18) + "\"");

        // Act
        var error = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.PutAsync("a", "x", content, cancellationToken: harness.Token));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        connection.IsOpen.ShouldBeTrue();
        (await connection.GetAsync("a", "x", harness.Token)).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Large query responses bound formatter allocations before the header")]
    [InlineData("SELECT * FROM items", DocumentProtocolMessageType.Document)]
    [InlineData("SELECT text, marker FROM items", DocumentProtocolMessageType.Document)]
    public async Task QueryAsync_LargeResult_ShouldBoundFormatterAllocationsBeforeHeader(string statement, DocumentProtocolMessageType rowType)
    {
        // Arrange: 32 MiB of encoded rows exceeds the 8 MiB pre-header allocation budget.
        // DocumentRowWriterTests separately measures peak backing-buffer capacity directly.
        // This raw peer discards rows while verifying every frame and its distinct marker.
        // Neither check bounds InMemory transport queues when a peer stops reading.
        // The statement budget includes 32 MiB each of source JSON, raw star images and
        // independent output ownership. Admit that storage while retaining the formatter's 8 MiB assertion.
        const int rowCount = 128;
        await using var harness = await DocumentClientTestHarness.StartAsync(
            configureEngine: builder => builder.Options.StatementMemoryBudget = 256L * 1024 * 1024);
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            var collection = await session.CreateCollectionAsync("items", harness.Token);
            await using var transaction = await session.BeginTransactionAsync(harness.Token);
            string text = new('x', 256 * 1024);
            for (int index = 0; index < rowCount; index++)
            {
                byte[] content = Encoding.UTF8.GetBytes("{\"text\":\"" + text + "\",\"marker\":" + index + "}");
                await collection.PutAsync(session, index.ToString("D4"), content, cancellationToken: harness.Token);
            }
            await transaction.CommitAsync(harness.Token);
        }
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        using var buffers = new ResponseBufferRecorder();

        // Act
        await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute, new DocumentProtocolExecuteMessage(statement, "{}"u8.ToArray()).Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.ResultHeader);
        for (int index = 0; index < rowCount; index++)
        {
            var frame = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            frame.Type.ShouldBe((ProtocolMessageType)rowType);
            ReadMarker(frame.Payload.Span, rowType == DocumentProtocolMessageType.Document).ShouldBe(index);
        }
        var complete = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        await WriteAsync(ProtocolMessageType.Ping, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Pong);

        // Assert: counts prove the entire large result was exercised; no elapsed-time assertion.
        _output.WriteLine($"Rows={buffers.RowCount}; frames={buffers.FrameCount}; peak frame={buffers.PeakFrameBytes}; allocated before header={buffers.BytesAllocatedBeforeHeader} bytes.");
        DocumentProtocolCompleteMessage.Decode(complete.Payload.Span).ResultCount.ShouldBe(rowCount);
        buffers.BaselineCount.ShouldBe(1);
        buffers.FrameCount.ShouldBe(rowCount + 2);
        buffers.RowCount.ShouldBe(rowCount);
        buffers.PeakFrameBytes.ShouldBeInRange(256 * 1024, 257 * 1024);
        buffers.BytesAllocatedBeforeHeader.ShouldBeLessThan(8L * 1024 * 1024);

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
    }

    private static int ReadMarker(ReadOnlySpan<byte> payload, bool wholeDocument)
    {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 256 });
        if (!wholeDocument)
        {
            reader.Read(); // Array.
            reader.Read(); // Text.
            reader.Read(); // Marker.
            return reader.GetInt32();
        }
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("marker"))
            {
                reader.Read();
                return reader.GetInt32();
            }
        }
        return -1;
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Failure after partial rows discards the result and preserves the session")]
    public async Task QueryAsync_MidStreamSizeFailure_ShouldThrowTypedErrorAndReuseSession()
    {
        // Arrange: repeating the escaped control value makes the second projection exceed one frame.
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"text\":\"small\"}"u8.ToArray(), cancellationToken: harness.Token);
        byte[] large = Encoding.UTF8.GetBytes("{\"text\":\"" + new string('\u0001', 1536 * 1024).Replace("\u0001", "\\u0001", StringComparison.Ordinal) + "\"}");
        await connection.PutAsync("items", "b", large, cancellationToken: harness.Token);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        using var buffers = new ResponseBufferRecorder();

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.QueryAsync("SELECT text, text AS copy FROM items", cancellationToken: harness.Token));

        // Assert: a real server sent header and first row before the terminal Failure, no Complete.
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        buffers.FrameCount.ShouldBe(2);
        buffers.RowCount.ShouldBe(1);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT text FROM items WHERE text = 'small'", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("small");
        await connection.DisposeAsync();
        await using var again = await harness.Client.ConnectAsync(harness.Token);
        (await again.GetCollectionsAsync(harness.Token)).ShouldBe(["items"]);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }
}
