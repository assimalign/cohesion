using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphFunctionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Functions: scalar, composite, CAST and temporal results round-trip over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_CoreFunctions_ShouldPreserveValuesAndTypesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name:'Alice',age:42})-[:KNOWS]->(:Person {name:'Bob'})", cancellationToken: harness.Token);
        // Act
        var result = await connection.QueryAsync("MATCH p=(a {name:'Alice'})-[r]->(b) RETURN upper(a.name) AS name, mod(a.age,5) AS rem, CAST('42' AS INTEGER) AS i, CURRENT_DATE AS d, CURRENT_TIMESTAMP AS t, labels(a) AS labels, keys(a) AS keys, properties(a) AS props, startNode(r).name AS start, endNode(r).name AS target, path_length(p) AS len", cancellationToken: harness.Token);
        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(["name","rem","i","d","t","labels","keys","props","start","target","len"]);
        result.Columns[0].Type.ShouldBe(DatabaseType.String); result.Columns[2].Type.ShouldBe(DatabaseType.Int32);
        result.Columns[3].Type.ShouldBe(DatabaseType.Date); result.Columns[4].Type.ShouldBe(DatabaseType.DateTimeOffset);
        var row = result.ShouldHaveSingleItem();
        row[0].ShouldBe("ALICE"); row[1].ShouldBeOfType<long>().ShouldBe(2L); row[2].ShouldBeOfType<int>().ShouldBe(42);
        var timestamp = row[4].ShouldBeOfType<DateTimeOffset>(); timestamp.Offset.ShouldBe(TimeSpan.Zero);
        row[3].ShouldBeOfType<DateOnly>().ShouldBe(DateOnly.FromDateTime(timestamp.UtcDateTime));
        row[5].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { "Person" });
        row[6].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { "age", "name" });
        row[7].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["age"].ShouldBe(42L);
        row[8].ShouldBe("Alice"); row[9].ShouldBe("Bob"); row[10].ShouldBe(1L);
        var identity = (await connection.QueryAsync("MATCH (a {name:'Alice'}) RETURN element_id(a)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0];
        (await connection.QueryAsync("MATCH (a {name:'Alice'}) RETURN elementId(a)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(identity);
        var temporal = await connection.QueryAsync("FOR s IN ['2026-10-19','2026-10-18'] RETURN CAST(s AS DATE) AS d ORDER BY d", cancellationToken: harness.Token);
        temporal.Select(row => row[0]).ShouldBe(new object?[] { new DateOnly(2026,10,18), new DateOnly(2026,10,19) });
        var conversions = await connection.QueryAsync("RETURN toInteger(2.5), toInteger('bad'), toFloat('bad'), toBoolean('bad'), round(-2.5)", cancellationToken: harness.Token);
        conversions.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 2L, null, null, null, -3d });
        var nested = await connection.QueryAsync("RETURN properties({nested:{x:1}}).nested AS m, properties({items:[1,2]}).items AS l", cancellationToken: harness.Token);
        nested.ShouldHaveSingleItem()[0].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["x"].ShouldBe(1L);
        nested[0][1].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 1L, 2L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Functions: runtime errors and entity gates precede output and keep the same pooled session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_FunctionFailures_ShouldReuseSessionAndRejectBeforeWritesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        Guid sessionId;
        await using (var connection = await harness.Client.ConnectAsync(harness.Token))
        {
            await connection.ExecuteAsync("INSERT (:Person {age:42})-[:T]->(:Person)", cancellationToken: harness.Token);
            sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
            // Act / Assert
            foreach (var (source, code) in new[]
            {
                ("MATCH (a:Person) RETURN upper(a.age)", "CG02013"),
                ("RETURN sqrt(-1)", "CG02013"),
                ("RETURN CAST(2.5 AS INTEGER)", "CG02013"),
                ("RETURN CAST('1e-1000' AS DOUBLE)", "CG02013"),
                ("RETURN CAST(1e-100 AS REAL)", "CG02013"),
                ("INSERT (:Unwritten {at:CURRENT_TIMESTAMP})", "CG03003"),
                ("INSERT (:Unwritten) RETURN toBoolean(1.5)", "CG03003"),
                ("INSERT (:Unwritten) RETURN CAST(TRUE AS INTEGER)", "CG03003"),
                ("MATCH p=()-[r]->() INSERT (:Unwritten) RETURN nodes(p)", "CG05001"),
                ("MATCH p=()-[r]->() INSERT (:Unwritten) RETURN edges(p)", "CG05001"),
                ("MATCH p=()-[r]->() INSERT (:Unwritten) RETURN relationships(p)", "CG05001"),
                ("MATCH ()-[r]->() INSERT (:Unwritten) RETURN startNode(r)", "CG05001"),
                ("MATCH ()-[r]->() INSERT (:Unwritten) RETURN endNode(r)", "CG05001"),
                ("MATCH p=()-[r]->() INSERT (:Unwritten) RETURN nodes(p)[0]", "CG05001"),
                ("MATCH (a:Person) INSERT (:Unwritten) RETURN properties({n:a})", "CG05001"),
                ("MATCH (a:Person) INSERT (:Unwritten) RETURN properties({m:{n:a}}).m", "CG05001"),
            })
            {
                var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(source, cancellationToken: harness.Token));
                error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure); error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
                connection.IsOpen.ShouldBeTrue();
            }
            (await connection.QueryAsync("MATCH (a:Unwritten) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        }
        await using var reused = await harness.Client.ConnectAsync(harness.Token);
        (await reused.QueryAsync("RETURN upper('ok')", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe("OK");
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Functions: unnegotiated composite calls and value errors send Error before ResultHeader")]
    public async Task Execute_FunctionErrors_ShouldPrecedeResultHeaderAsync()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var local = await harness.Database.CreateSessionAsync(harness.Token);
        await local.ExecuteAsync("INSERT (:P {age:42})", cancellationToken: harness.Token);
        await using var transport = await harness.ConnectionFactory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        await SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "tester").Encode());
        (await ReadAsync()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        channel.SetAgreement(ProtocolReadyMessage.Decode((await ReadAsync()).Payload.Span));
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (var (source, code) in new[]
        {
            ("MATCH (a:P) RETURN labels(a)", "CG07016"),
            ("MATCH (a:P) RETURN keys(a)", "CG07016"),
            ("MATCH (a:P) RETURN properties(a)", "CG07016"),
            ("RETURN properties({nested:{x:1}}).nested", "CG07016"),
            ("MATCH (a:P) RETURN upper(a.age)", "CG02013"),
            ("RETURN sqrt(-1)", "CG02013"),
        })
        {
            await ExecuteAsync(source);
            var frame = await ReadAsync(); frame.Type.ShouldBe(ProtocolMessageType.Error);
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure); error.Message.ShouldStartWith(code + ": ", Case.Sensitive);
        }
        await ExecuteAsync("RETURN CURRENT_TIMESTAMP AS t");
        (await ReadAsync()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultHeader);
        (await ReadAsync()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultRow);
        (await ReadAsync()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);

        async Task ExecuteAsync(string source) => await SendAsync((ProtocolMessageType)GraphProtocolMessageType.Execute, new GraphProtocolExecuteMessage(source, new Dictionary<string, byte[]>()).Encode());
        async Task SendAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token); await channel.Writer.FlushAsync(harness.Token); }
        async Task<ProtocolFrame> ReadAsync() => (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
    }
}
