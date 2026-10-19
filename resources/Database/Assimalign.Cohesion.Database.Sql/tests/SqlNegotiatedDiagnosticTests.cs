using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNegotiatedDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - session result double sends warnings to the real client and filters Information")]
    public async Task ExecuteAsync_SuccessWarning_ShouldReachTypedResultAndExcludeInformation()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new() { Database = ServerTestHarness.DatabaseName, EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        var serverSession = (SqlDatabaseServerSession)harness.Server.Sessions.Single();
        int calls = 0;
        serverSession.ExecuteResultOverride = () => { calls++; return new DiagnosticSessionResult(); };
        // Act
        var first = await connection.QueryAsync("SELECT 1", cancellationToken: TestTimeout.Token());
        var second = await connection.QueryAsync("SELECT 1", cancellationToken: TestTimeout.Token());
        // Assert
        calls.ShouldBe(2);
        first.Diagnostics.ShouldHaveSingleItem().ShouldBe(new ProtocolDiagnostic(ProtocolDiagnosticSeverity.Warning, "SQLTEST001", "Test warning", 0, 6, 1));
        second.Diagnostics.ShouldBe(first.Diagnostics);
        connection.IsOpen.ShouldBeTrue();
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - mixed minors and omitted capabilities execute without unsolicited frames")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task Handshake_MixedMinors_ShouldGateDiagnostics(ushort minor, bool diagnostics)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        var startup = new ProtocolStartupMessage(new(2, minor), ServerTestHarness.DatabaseName, "tester") { Capabilities = diagnostics ? ["diagnostics"] : [] };
        // Act
        await client.SendAsync(ProtocolMessageType.Startup, startup.Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);
        var ready = await client.ExpectAsync(ProtocolMessageType.Ready);
        var agreement = ProtocolReadyMessage.Decode(ready.Payload.Span);
        await client.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, new ProtocolExecuteMessage("SELECT absent FROM users", new System.Collections.Generic.Dictionary<string, byte[]>()).Encode());
        // Assert
        agreement.Version.ShouldBe(new(2,minor));
        agreement.Capabilities.Contains("diagnostics").ShouldBe(diagnostics);
        if (minor == 0) { ready.Payload.IsEmpty.ShouldBeTrue(); }
        if (diagnostics)
        {
            var frame = await client.ExpectAsync(ProtocolMessageType.Diagnostics);
            ProtocolDiagnosticsMessage.Decode(frame.Payload.Span).Entries.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE015");
        }
        var error = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(error.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        await client.SendAsync(ProtocolMessageType.Ping);
        await client.ExpectAsync(ProtocolMessageType.Pong);
    }
}
