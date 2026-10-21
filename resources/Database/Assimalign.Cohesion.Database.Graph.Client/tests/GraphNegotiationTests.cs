using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphNegotiationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph.Client] - mixed minors and omitted capabilities preserve graph exchanges")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task Handshake_MixedMinors_ShouldGateFailuresAndSuccessWarnings(ushort minor, bool diagnostics)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var transport = await harness.ConnectionFactory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, true);
        var startup = new ProtocolStartupMessage(new(2, minor), "graph", "tester") { Capabilities = diagnostics ? ["diagnostics"] : [] };
        // Act
        await SendAsync(ProtocolMessageType.Startup, startup.Encode());
        (await ReadAsync()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        var ready = await ReadAsync();
        ready.Type.ShouldBe(ProtocolMessageType.Ready);
        var agreement = ProtocolReadyMessage.Decode(ready.Payload.Span);
        channel.SetAgreement(agreement);
        // Assert
        agreement.Version.ShouldBe(new(2, minor));
        agreement.Capabilities.Contains("diagnostics").ShouldBe(diagnostics);
        if (minor == 0) { ready.Payload.IsEmpty.ShouldBeTrue(); }
        await ExecuteAsync("MATCH ANY SHORTEST (a)->(b) RETURN a UNION MATCH (c) RETURN c");
        if (diagnostics)
        {
            var diagnostic = await ReadAsync();
            diagnostic.Type.ShouldBe(ProtocolMessageType.Diagnostics);
            ProtocolDiagnosticsMessage.Decode(diagnostic.Payload.Span).Entries.Count.ShouldBe(2);
        }
        var error = await ReadAsync();
        error.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(error.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        await ExecuteAsync("MATCH (n:Absent) RETURN n.name");
        (await ReadAsync()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultHeader);
        if (diagnostics)
        {
            var warning = await ReadAsync();
            warning.Type.ShouldBe(ProtocolMessageType.Diagnostics);
            ProtocolDiagnosticsMessage.Decode(warning.Payload.Span).Entries.ShouldHaveSingleItem().Code.ShouldBe("COHDBG010");
        }
        (await ReadAsync()).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);
        await SendAsync(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty);
        (await ReadAsync()).Type.ShouldBe(ProtocolMessageType.Pong);

        async Task SendAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
        async Task<ProtocolFrame> ReadAsync() => (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        Task ExecuteAsync(string statement) => SendAsync((ProtocolMessageType)GraphProtocolMessageType.Execute,
            new GraphProtocolExecuteMessage(statement, new Dictionary<string, byte[]>()).Encode());
    }
}
