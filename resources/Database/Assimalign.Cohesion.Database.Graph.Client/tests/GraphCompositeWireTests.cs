using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Graph.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphCompositeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Comprehensions: scalar entity-list projections cross Execute over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ComprehensionsAndSlices_ShouldPreserveScalarAdmissionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:N {k:1})-[:T {w:2}]->(:N {k:2})-[:T {w:3}]->(:N {k:3})", cancellationToken: harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (string source in new[]
        {
            "MATCH (a:N {k:1})-[rs:T]->{2}(b) RETURN [r IN rs | r.w]",
            "MATCH p=(a:N {k:1})-[:T]->{2}(b) RETURN [r IN relationships(p) | r.w]",
            "MATCH (a:N {k:1})-[rs:T]->{2}(b) WITH [r IN rs | {w:r.w}] AS values RETURN [v IN values | v.w]",
            "MATCH (a:N {k:1})-[rs:T]->{2}(b) RETURN [r IN rs[0..] | [r IN [r] | r.w][0]]",
        })
        {
            var row = (await connection.QueryAsync(source, cancellationToken: harness.Token)).ShouldHaveSingleItem();
            row[0].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L, 3L });
        }
        var nodes = (await connection.QueryAsync("MATCH p=(a:N {k:1})-[:T]->{2}(b) RETURN [n IN nodes(p)[1..] | n.k]", cancellationToken: harness.Token)).ShouldHaveSingleItem();
        nodes[0].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L, 3L });
        var parameters = new Dictionary<string, object?> { ["xs"] = new object?[] { 1L, null, 3L }, ["from"] = -2L };
        var values = (await connection.QueryAsync("RETURN [x IN $xs WHERE x IS NOT NULL | x*2], $xs[$from..], [x IN NULL | x], [1][..NULL]", parameters, harness.Token)).ShouldHaveSingleItem();
        values[0].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L, 6L });
        values[1].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { null, 3L });
        values[2].ShouldBeNull(); values[3].ShouldBeNull();
        foreach (string expression in new[] { "[r IN rs | r]", "[r IN rs WHERE FALSE]", "rs[0..]", "[r IN rs | {r:r}]" })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(
                "MATCH (a:N {k:1})-[rs:T]->{2}(b) INSERT (:Unwritten) RETURN " + expression, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        }
        var badBound = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("INSERT (:Unwritten) RETURN [1]['x'..]", cancellationToken: harness.Token));
        badBound.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02014");
        foreach (var (query, code) in new[]
        {
            ("RETURN [1][..]", "CG00012"),
            ("EXPLAIN RETURN [1][1.5..]", "CG02014"),
            ("RETURN [x IN [] WHERE 1]", "CG02013"),
            ("EXPLAIN RETURN [x IN NULL WHERE 1]", "CG02013"),
            ("MERGE (n:Rejected {k:[x IN [1] | x]})", "CK00001"),
        })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(query, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
        await connection.ExecuteAsync("UNWIND [1,2,1] AS k MERGE (n:CollectionMerge {k:[x IN [k] | x*2][0]}) ON CREATE SET n.c=[x IN [k] | x+10][0] ON MATCH SET n.m=[x IN [k] | x+20][0]", cancellationToken: harness.Token);
        var merged = await connection.QueryAsync("MATCH (n:CollectionMerge) RETURN n.k,n.c,n.m ORDER BY n.k", cancellationToken: harness.Token);
        merged.Count.ShouldBe(2);
        merged[0].ToArray().ShouldBe(new object?[] { 2L, 11L, 21L });
        merged[1].ToArray().ShouldBe(new object?[] { 4L, 12L, null });
        (await connection.QueryAsync("RETURN all(x IN [] WHERE TRUE)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(true);
        (await connection.QueryAsync("MATCH (n:Unwritten) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        var explain = await connection.QueryAsync("EXPLAIN MATCH (a)-[rs]->{2}(b) RETURN [r IN rs | r.w]", cancellationToken: harness.Token);
        explain.ShouldNotBeEmpty();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composite values: malformed request parameters are protocol violations")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Execute_MalformedCompositeParameter_ShouldReportProtocolViolation(int form)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var transport = await harness.ConnectionFactory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "graph", "tester")
            { Capabilities = [GraphProtocol.CompositeValues.Name] }.Encode());
        (await Read()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        var ready = await Read(); channel.SetAgreement(ProtocolReadyMessage.Decode(ready.Payload.Span));
        byte[] component = form switch
        {
            0 => [255],
            1 => [1, 255, 255, 255, 127],
            _ => Enumerable.Repeat(new byte[] { 1, 1, 0, 0, 0 }, GraphValueCodec.MaxDepth + 1)
                .SelectMany(bytes => bytes).Concat(GraphValueCodec.Encode(null)).ToArray(),
        };
        // Act
        await Send((ProtocolMessageType)GraphProtocolMessageType.Execute,
            new GraphProtocolExecuteMessage("RETURN $p", new Dictionary<string, byte[]> { ["p"] = component }).Encode());
        // Assert
        var error = await Read(); error.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(error.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token); await channel.Writer.FlushAsync(harness.Token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Composite values: nested parameters and results round-trip on scalar and path exchanges")]
    public async Task Query_CompositeValues_ShouldRoundTripNestedRuntimeTypes()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        object?[] values = [null, (sbyte)1, (short)2, 3, 4L, 0.1f, 0.1d, 0.1m, true, "name", (byte)1, (ushort)2, 3U, ulong.MaxValue];
        var parameters = new Dictionary<string, object?> { ["l"] = values, ["m"] = new Dictionary<string, object?> { ["items"] = values } };
        // Act
        var result = await connection.QueryAsync("RETURN $l, $m, [1,NULL,{k:[2]}]", parameters, harness.Token);
        // Assert
        var row = result.ShouldHaveSingleItem();
        var list = row[0].ShouldBeOfType<object?[]>();
        list.ShouldBe(values);
        list.Select(value => value?.GetType()).ShouldBe(values.Select(value => value?.GetType()));
        var map = row[1].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>();
        map!["items"].ShouldBeOfType<object?[]>().ShouldBe(values);
        foreach (object? value in values.Skip(10))
        {
            var unsigned = (await connection.QueryAsync("INSERT (a:Unsigned {k:$p}) RETURN a.k",
                new Dictionary<string, object?> { ["p"] = value }, harness.Token)).ShouldHaveSingleItem()[0];
            unsigned.ShouldBe(value); unsigned?.GetType().ShouldBe(value?.GetType());
        }
        await connection.ExecuteAsync("INSERT (:P {k:1})", cancellationToken: harness.Token);
        int paths = 0;
        await foreach (var path in connection.QueryPathsAsync("MATCH (n:P) WHERE n.k IN $xs RETURN n",
            new Dictionary<string, object?> { ["xs"] = new List<long> { 1 } }, harness.Token)) { paths++; }
        paths.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composite values: unnegotiated results fail before a header and reuse the session")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Execute_WithoutCapability_ShouldRejectBeforeHeader(ushort minor, bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var transport = await harness.ConnectionFactory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(new(2, minor), "graph", "tester").Encode());
        (await Read()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        var ready = await Read();
        channel.SetAgreement(ProtocolReadyMessage.Decode(ready.Payload.Span));
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act
        foreach (string statement in new[] { "MATCH (a)-[r:T*0]->(b) RETURN r", "MATCH (a)-[r]->{1,3}(b) RETURN r", "MATCH ((x)->(y)){1,3} RETURN *", "MATCH (a)-[r]->{1,3}(b) WITH r RETURN r", "LET l = [1,2] RETURN l", "LET l = [[1]] FOR x IN l RETURN x", "LET l = [1] WITH l RETURN l", "RETURN [1] AS l NEXT RETURN l", "RETURN [1,NULL]", "RETURN NULL AS x UNION RETURN [1] AS x", "RETURN NULL AS x UNION ALL MATCH (n:Missing) RETURN [n] AS x", "RETURN {k:1}", "INSERT (:Unwritten) RETURN [1]", "INSERT (:Unwritten) RETURN {k:1}" })
        {
            await Execute(statement);
            var failure = await Read();
            // Assert
            failure.Type.ShouldBe(ProtocolMessageType.Error);
            var error = ProtocolErrorMessage.Decode(failure.Payload.Span);
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Message.ShouldContain("CG07016", Case.Sensitive);
        }
        await Execute("MATCH (n:Unwritten) RETURN 1");
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultHeader);
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);
        await Execute("SHOW LABELS");
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultHeader);
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        await Execute("RETURN 42");
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultHeader);
        var scalar = await Read();
        scalar.Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultRow);
        scalar.Payload.ToArray().ShouldBe(Assimalign.Cohesion.Database.Types.DatabaseValueCodec.EncodeComponent(42L));
        (await Read()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);

        async Task Send(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        { await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token); await channel.Writer.FlushAsync(harness.Token); }
        async Task<ProtocolFrame> Read() => (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        Task Execute(string statement) => Send((ProtocolMessageType)GraphProtocolMessageType.Execute,
            new GraphProtocolExecuteMessage(statement, new Dictionary<string, byte[]>()).Encode());
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Values: entity member and subscript reads pass the Execute gate")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_EntityPropertyExpressions_ShouldReturnScalarProperties(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name:'Alice'}), (:Person {name:'Bob'})", cancellationToken: harness.Token);
        // Act / Assert
        var rows = await connection.QueryAsync("MATCH (a:Person) OPTIONAL MATCH (a)->(b) RETURN (a).name, COALESCE(b,a).name, a['name']", cancellationToken: harness.Token);
        rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob" }, ignoreOrder: true);
        foreach (var row in rows) { row[1].ShouldBe(row[0]); row[2].ShouldBe(row[0]); }
        await connection.ExecuteAsync("INSERT (:P)-[:T {name:'edge'}]->(:P)", cancellationToken: harness.Token);
        var edge = (await connection.QueryAsync("MATCH (a)-[r:T]->(b) RETURN (r).name, r['name']", cancellationToken: harness.Token)).ShouldHaveSingleItem();
        edge[0].ShouldBe("edge"); edge[1].ShouldBe("edge");
        var conditional = await connection.QueryAsync("RETURN 42, CASE 1 WHEN 1 THEN 2 ELSE [3] END, {k:1}, 4", cancellationToken: harness.Token);
        var mixed = conditional.ShouldHaveSingleItem();
        mixed[0].ShouldBe(42L); mixed[1].ShouldBe(2L); mixed[2].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>(); mixed[3].ShouldBe(4L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composite values: constructed bounds roll back writes and retain the session")]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Execute_ConstructedBound_ShouldRollBackAndReuse(bool tcp, int form)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        string value = form == 0 ? new string('[', 65) + "1" + new string(']', 65) : "[$p,$p]";
        var parameters = new Dictionary<string, object?>();
        if (form != 0) { parameters["p"] = form == 1 ? new object?[40_000] : new string('a', 9 * 1024 * 1024); }
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(
            "INSERT (:Unwritten) RETURN " + value, parameters, harness.Token));
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("CG02013", Case.Sensitive);
        (await connection.QueryAsync("MATCH (n:Unwritten) RETURN 1", cancellationToken: harness.Token)).ShouldBeEmpty();
        (await connection.QueryAsync("RETURN 42", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(42L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Parser failures: incomplete lists and nested subscripts retain the session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_InvalidNesting_ShouldFailAndReuse(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (string query in new[] { "RETURN [", "RETURN [[", "MATCH (a) WHERE a.x IN [", "RETURN {k:[" })
        {
            foreach (string suffix in new[] { "", " -- comment" })
            {
                var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(query + suffix, cancellationToken: harness.Token));
                error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
                error.Message.ShouldContain("CG00002", Case.Sensitive);
            }
        }
        // The server parses on a thread pool thread whose stack is 8 MB on Linux against 1 to 1.5 MB
        // on Windows, so the depth must defeat 8 MB (GraphLabelChainWireTests uses the same depth).
        const int depth = 500_000;
        var deep = "RETURN " + string.Concat(Enumerable.Repeat("0[", depth)) + "0" + new string(']', depth);
        var nesting = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(deep, cancellationToken: harness.Token));
        nesting.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        nesting.Message.ShouldContain("CG05008", Case.Sensitive);
        (await connection.QueryAsync("RETURN 42", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(42L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Composite values: unnegotiated parameter rejection happens before exchange creation")]
    public void Create_WithoutCapability_ShouldRefuseCompositeParametersLocally()
    {
        // Arrange / Act / Assert
        foreach (object value in new object[] { new List<long> { 1 }, new Dictionary<string, object?> { ["k"] = 1 } })
        {
            var error = Should.Throw<GraphClientException>(() => GraphRequestEncoder.Create("RETURN $p", new Dictionary<string, object?> { ["p"] = value }));
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        }
    }
}
