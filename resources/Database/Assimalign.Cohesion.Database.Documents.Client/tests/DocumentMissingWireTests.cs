using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentMissingWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Both transports omit MISSING members, preserve explicit null and expose MISSING on client rows")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_MissingAndNull_ShouldPreserveBothStatesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"name\":\"alpha\",\"details\":{\"score\":1}}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "b", "{\"name\":\"delta\"}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "c", "{\"name\":\"null\",\"details\":{\"score\":null}}"u8.ToArray(), cancellationToken: harness.Token);
        const string query = "SELECT name, details.score AS score FROM items";
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);

        // Act: inspect actual model frames before using the client decoder.
        await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute, new DocumentProtocolExecuteMessage(query, "{}"u8.ToArray()).Encode());
        var header = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        header.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.ResultHeader);
        DocumentProtocolResultHeaderMessage.Decode(header.Payload.Span).WholeDocuments.ShouldBeFalse();
        for (int index = 0; index < 3; index++)
        {
            var frame = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Document);
            using var json = JsonDocument.Parse(DocumentProtocolResultMessage.Decode(frame.Payload.Span).Json);
            if (index == 0) { json.RootElement.GetProperty("score").GetInt32().ShouldBe(1); }
            else if (index == 1)
            {
                json.RootElement.GetProperty("name").GetString().ShouldBe("delta");
                json.RootElement.TryGetProperty("score", out _).ShouldBeFalse();
            }
            else { json.RootElement.GetRawText().ShouldContain("\"score\":null", Case.Sensitive); }
        }
        var complete = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        complete.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        DocumentProtocolCompleteMessage.Decode(complete.Payload.Span).ResultCount.ShouldBe(3);
        var result = await connection.QueryAsync(query, cancellationToken: harness.Token);

        // Assert
        result[0].IsMissing(1).ShouldBeFalse();
        result[1].SupportsMissing.ShouldBeTrue();
        result[1].IsMissing(1).ShouldBeTrue();
        result[1].IsNull(1).ShouldBeFalse();
        result[1].GetValue(1).ShouldBeNull();
        result[1]["score"].ShouldBeNull();
        result[2].IsMissing(1).ShouldBeFalse();
        result[2].IsNull(1).ShouldBeTrue();
        var whole = await connection.QueryAsync("SELECT * FROM items", cancellationToken: harness.Token);
        whole.Count.ShouldBe(3);
        foreach (var row in whole) { row.SupportsMissing.ShouldBeTrue(); row.IsMissing(0).ShouldBeFalse(); }

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
    }
}
