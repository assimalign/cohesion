using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentProjectionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Advertised projection clauses emit one JSON value and accurate completion counts")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ProjectionClauses_ShouldPreserveFramesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"name\":\"alpha\",\"category\":\"a\"}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "b", "{\"name\":\"beta\",\"category\":\"a\"}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "c", "{\"name\":\"gamma\",\"category\":\"b\"}"u8.ToArray(), cancellationToken: harness.Token);
        (string Query, bool Bare, string[] Json)[] cases =
        [
            ("SELECT DISTINCT category FROM items ORDER BY category", false, ["{\"category\":\"a\"}", "{\"category\":\"b\"}"]),
            ("SELECT VALUE name FROM items WHERE category = 'b'", true, ["\"gamma\""]),
            ("SELECT DISTINCT RAW category FROM items ORDER BY category", true, ["\"a\"", "\"b\""]),
            ("SELECT VALUE {n: name, t: [category, {x: NULL}, MISSING]} FROM items WHERE name = 'alpha'", true,
                ["{\"n\":\"alpha\",\"t\":[\"a\",{\"x\":null},null]}"]),
            ("SELECT [name, category] AS pair FROM items WHERE name = 'beta'", false, ["{\"pair\":[\"beta\",\"a\"]}"]),
        ];
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        // Act / Assert: both actual frames and public client results.
        foreach (var test in cases)
        {
            await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute, new DocumentProtocolExecuteMessage(test.Query, "{}"u8.ToArray()).Encode());
            var header = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            header.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.ResultHeader);
            DocumentProtocolResultHeaderMessage.Decode(header.Payload.Span).WholeDocuments.ShouldBe(test.Bare);
            foreach (string expected in test.Json)
            {
                var frame = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
                frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Document);
                Encoding.UTF8.GetString(DocumentProtocolResultMessage.Decode(frame.Payload.Span).Json.Span).ShouldBe(expected);
            }
            var complete = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            complete.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
            DocumentProtocolCompleteMessage.Decode(complete.Payload.Span).ResultCount.ShouldBe(test.Json.Length);
            var result = await connection.QueryAsync(test.Query, cancellationToken: harness.Token);
            result.Count.ShouldBe(test.Json.Length);
            if (test.Bare)
            {
                result.Columns.ShouldHaveSingleItem().Name.ShouldBe("value");
                result.Documents.Select(value => value.GetRawText()).ShouldBe(test.Json);
            }
            else
            {
                using var json = JsonDocument.Parse(test.Json[0]);
                result[0][0].ShouldNotBeNull();
                if (json.RootElement.TryGetProperty("pair", out var pair))
                {
                    JsonElement.DeepEquals(result[0][0].ShouldBeOfType<JsonElement>(), pair).ShouldBeTrue();
                }
                else { result.Select(row => row[0]).ShouldBe(new object?[] { "a", "b" }); }
            }
        }

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
    }
}
