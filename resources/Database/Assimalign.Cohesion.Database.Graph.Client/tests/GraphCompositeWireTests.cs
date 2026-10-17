using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Graph.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphCompositeWireTests
{
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
        await connection.ExecuteAsync("INSERT (:P {k:1})", cancellationToken: harness.Token);
        int paths = 0;
        await foreach (var path in connection.QueryPathsAsync("MATCH (n:P) WHERE n.k IN $xs RETURN n",
            new Dictionary<string, object?> { ["xs"] = new List<long> { 1 } }, harness.Token)) { paths++; }
        paths.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composite values: unnegotiated results fail before a header and reuse the session")]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Execute_WithoutCapability_ShouldRejectBeforeHeader(ushort minor)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var transport = await harness.ConnectionFactory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        await Send(ProtocolMessageType.Startup, new ProtocolStartupMessage(new(2, minor), "graph", "tester").Encode());
        (await Read()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await Send(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        var ready = await Read();
        channel.SetAgreement(ProtocolReadyMessage.Decode(ready.Payload.Span));
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act
        foreach (string statement in new[] { "RETURN [1,NULL]", "RETURN {k:1}" })
        {
            await Execute(statement);
            var failure = await Read();
            // Assert
            failure.Type.ShouldBe(ProtocolMessageType.Error);
            var error = ProtocolErrorMessage.Decode(failure.Payload.Span);
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Message.ShouldContain("COHDBG016", Case.Sensitive);
        }
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
