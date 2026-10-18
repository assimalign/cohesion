using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Execution;
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
        await connection.PutAsync("items", "a", "{\"name\":\"alpha\",\"category\":\"a\",\"x\":null}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "b", "{\"name\":\"beta\",\"category\":\"a\"}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "c", "{\"name\":\"gamma\",\"category\":\"b\"}"u8.ToArray(), cancellationToken: harness.Token);
        (string Query, bool Bare, string[] Json)[] cases =
        [
            ("SELECT DISTINCT VALUE x FROM items ORDER BY x", true, ["null"]),
            ("SELECT DISTINCT RAW x FROM items ORDER BY x", true, ["null"]),
            ("SELECT VALUE x FROM items ORDER BY x", true, ["null", "null", "null"]),
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
            await using var session = await harness.Database.CreateSessionAsync(harness.Token);
            await using var embedded = (await session.ExecuteAsync(test.Query, cancellationToken: harness.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
            int index = 0;
            await foreach (var row in embedded.GetRowsAsync(harness.Token))
            {
                if (test.Bare)
                {
                    row.IsMissing(0).ShouldBeFalse();
                    result[index].IsMissing(0).ShouldBeFalse();
                    row.IsNull(0).ShouldBe(result[index].IsNull(0));
                    row.GetBytes(0).ToArray().ShouldBe(Encoding.UTF8.GetBytes(test.Json[index]));
                }
                index++;
            }
            index.ShouldBe(test.Json.Length);
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

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - GetBytes agrees in process, InMemory and TCP for named and bare values")]
    public async Task GetBytes_ResultShapes_ShouldAgreeAcrossTransportsAsync()
    {
        // Arrange
        const string text = "é東京<&😀\u2028\u2029";
        const string content = " \r\n{ \"name\" : \"" + text + "\", \"n\" : null } \t";
        (string Query, string Bytes)[] cases =
        [
            ("SELECT name FROM items", text),
            ("SELECT n FROM items", ""),
            ("SELECT MISSING AS n FROM items", ""),
            ("SELECT VALUE MISSING FROM items", "null"),
            ("SELECT * FROM items", content),
            ("SELECT VALUE name FROM items", "\"" + text + "\""),
            ("SELECT RAW name FROM items", "\"" + text + "\""),
            ("SELECT VALUE {'n':name} FROM items", "{\"n\":\"" + text + "\"}"),
            ("SELECT RAW [name] FROM items", "[\"" + text + "\"]"),
            ("SELECT {'n':name} AS o FROM items", "{\"n\":\"" + text + "\"}"),
            ("SELECT [name] AS a FROM items", "[\"" + text + "\"]"),
            ("SELECT VALUE NULL FROM items", "null"),
            ("SELECT RAW 42 FROM items", "42"),
            ("SELECT VALUE TRUE FROM items", "true"),
        ];
        foreach (bool tcp in new[] { false, true })
        {
            await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
            await using var connection = await harness.Client.ConnectAsync(harness.Token);
            await connection.CreateCollectionAsync("items", harness.Token);
            await connection.PutAsync("items", "a", Encoding.UTF8.GetBytes(content), cancellationToken: harness.Token);
            await using var session = await harness.Database.CreateSessionAsync(harness.Token);
            foreach (var test in cases)
            {
                // Act
                var wire = await connection.QueryAsync(test.Query, cancellationToken: harness.Token);
                await using var embedded = (await session.ExecuteAsync(test.Query, cancellationToken: harness.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                var rows = new List<DatabaseRow>();
                await foreach (var row in embedded.GetRowsAsync(harness.Token)) { rows.Add(row); }
                // Assert
                var bytes = rows.ShouldHaveSingleItem().GetBytes(0).ToArray();
                bytes.ShouldBe(Encoding.UTF8.GetBytes(test.Bytes));
                wire.ShouldHaveSingleItem().GetBytes(0).ToArray().ShouldBe(bytes);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Lone star preserves raw frames through ordering and DISTINCT")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_StarOrderingAndDistinct_ShouldPreserveRawFramesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        string[] content = [" {\"n\": 1} ", "\r\n{ \"n\" : 2 }\t", " {\"n\": 1.0} "];
        for (int i = 0; i < content.Length; i++)
        { await connection.PutAsync("items", i.ToString(), Encoding.UTF8.GetBytes(content[i]), cancellationToken: harness.Token); }
        (string Query, string[] Expected)[] cases =
        [
            ("SELECT * FROM items ORDER BY n DESC", [content[1], content[0], content[2]]),
            ("SELECT DISTINCT * FROM items", [content[0], content[1]]),
        ];
        // Act / Assert
        foreach (var test in cases)
        {
            var frames = await ReadFramesAsync(harness, test.Query);
            frames.Select(bytes => Encoding.UTF8.GetString(bytes)).ShouldBe(test.Expected);
            var rows = await connection.QueryAsync(test.Query, cancellationToken: harness.Token);
            rows.Select(row => Encoding.UTF8.GetString(row.GetBytes(0).Span)).ShouldBe(test.Expected);
            await using var session = await harness.Database.CreateSessionAsync(harness.Token);
            await using var embedded = (await session.ExecuteAsync(test.Query, cancellationToken: harness.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
            var bytes = new List<string>();
            await foreach (var row in embedded.GetRowsAsync(harness.Token)) { bytes.Add(Encoding.UTF8.GetString(row.GetBytes(0).Span)); }
            bytes.ShouldBe(test.Expected);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Constructor depth preserves SELECT diagnostics on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_DeepConstructors_ShouldHonorResultDepthAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{}"u8.ToArray(), cancellationToken: harness.Token);
        foreach (bool objects in new[] { false, true })
        {
            string Wrap(string value) => objects ? "{w: " + value + "}" : "[" + value + "]";
            foreach (int depth in new[] { 150, 255 })
            {
                using var parameter = JsonDocument.Parse(new string('[', depth) + "0" + new string(']', depth), new JsonDocumentOptions { MaxDepth = 256 });
                var parameters = new Dictionary<string, object?> { ["p"] = parameter.RootElement };
                // Act
                var result = await connection.QueryAsync("SELECT VALUE " + Wrap("@p") + " FROM items", parameters, harness.Token);
                // Assert
                var value = result.Documents.ShouldHaveSingleItem();
                (objects ? value.GetProperty("w") : value[0]).GetRawText().ShouldBe(parameter.RootElement.GetRawText());
                if (depth == 150) { continue; }
                foreach (string query in new[] { "SELECT VALUE " + Wrap(Wrap("@p")) + " FROM items", "SELECT " + Wrap("@p") + " AS w FROM items" })
                {
                    var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(query, parameters, harness.Token));
                    var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
                    diagnostic.Code.ShouldBe("COHDOC015");
                    diagnostic.Start.ShouldBe(query.StartsWith("SELECT VALUE", StringComparison.Ordinal) ? 13 : 7);
                    error.Message.ShouldContain("SELECT " + (objects ? "object" : "array") + " constructor values must be nested no deeper than 256 levels.", Case.Sensitive);
                }
            }
        }
        (await connection.QueryAsync("SELECT VALUE 1 FROM items", cancellationToken: harness.Token)).Count.ShouldBe(1);
    }

    private static async Task<List<byte[]>> ReadFramesAsync(DocumentClientTestHarness harness, string query)
    {
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute, new DocumentProtocolExecuteMessage(query, "{}"u8.ToArray()).Encode());
        var header = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        header.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.ResultHeader);
        DocumentProtocolResultHeaderMessage.Decode(header.Payload.Span).WholeDocuments.ShouldBeTrue();
        var rows = new List<byte[]>();
        while (true)
        {
            var frame = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            if (frame.Type == (ProtocolMessageType)DocumentProtocolMessageType.Complete)
            {
                DocumentProtocolCompleteMessage.Decode(frame.Payload.Span).ResultCount.ShouldBe(rows.Count);
                return rows;
            }
            frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Document);
            rows.Add(DocumentProtocolResultMessage.Decode(frame.Payload.Span).Json.ToArray());
        }

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
    }
}
