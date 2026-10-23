using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentCollectionDdlWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Collection refusal codes and spans match embedded execution over both transports")]
    [InlineData(false, "duplicate", true)]
    [InlineData(true, "duplicate", true)]
    [InlineData(false, "missing", true)]
    [InlineData(true, "missing", true)]
    [InlineData(false, "locked", true)]
    [InlineData(true, "locked", true)]
    [InlineData(false, "locked-conditional", true)]
    [InlineData(true, "locked-conditional", true)]
    [InlineData(false, "duplicate", false)]
    [InlineData(true, "duplicate", false)]
    [InlineData(false, "missing", false)]
    [InlineData(true, "missing", false)]
    [InlineData(false, "locked", false)]
    [InlineData(true, "locked", false)]
    public async Task Server_CollectionRefusal_ShouldPreserveDiagnosticsAndSessionAsync(bool tcp, string scenario, bool oql)
    {
        // Arrange: seed ownership through the owning assembly's existing test-only catalog access.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        IDuplexConnectionListener listener;
        IDuplexConnectionFactory factory;
        if (tcp)
        {
            listener = new TcpConnectionListener(new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            factory = new TcpConnectionFactory();
        }
        else
        {
            var memory = new InMemoryConnectionListener();
            listener = memory;
            factory = memory.CreateFactory();
        }
        await using var engine = await DocumentDatabaseEngine.CreateBuilder("ddl-wire").AddDatabase("documents")
            .AddServer(options => options.Listener = listener).BuildAsync(token);
        var database = await engine.OpenDatabaseAsync("documents", token);
        await using var embedded = await database.CreateSessionAsync(token);
        await embedded.CreateCollectionAsync("items", token);
        bool locked = scenario.StartsWith("locked", StringComparison.Ordinal);
        if (locked)
        {
            var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            var collection = database.Catalog.FindCollection("items", context.Snapshot).ShouldNotBeNull();
            await database.Catalog.SaveCollectionAsync(collection with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, token);
            await database.Coordinator.CommitAsync(context, token);
        }
        string name = scenario == "missing" ? "absent" : "items";
        var operation = scenario == "duplicate" ? DocumentProtocolOperation.CreateCollection : DocumentProtocolOperation.DropCollection;
        string statement = "-- DDL\n" + (scenario == "duplicate" ? "CREATE COLLECTION " :
            scenario == "locked-conditional" ? "DROP COLLECTION IF EXISTS " : "DROP COLLECTION ") + name + ";";
        string code = scenario == "duplicate" ? "CD03010" : locked ? "CD03011" : "CD01002";
        var original = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (oql) { await embedded.ExecuteAsync(statement, cancellationToken: token); }
            else if (operation == DocumentProtocolOperation.CreateCollection) { await embedded.CreateCollectionAsync(name, token); }
            else { await embedded.DropCollectionAsync(name, token); }
        });
        var server = engine.Servers.Single();
        await server.StartAsync(token);
        await using var transport = await factory.ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        Guid sessionId = server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        await WriteAsync((ProtocolMessageType)(oql ? DocumentProtocolMessageType.Execute : DocumentProtocolMessageType.Command),
            oql ? new DocumentProtocolExecuteMessage(statement, "{}"u8.ToArray()).Encode() : new DocumentProtocolCommandMessage(operation, name).Encode());
        var response = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();

        // Assert
        original.GetType().ShouldBe(locked ? typeof(DatabaseObjectLockedException) : typeof(DatabaseException));
        response.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
        var failure = DocumentProtocolFailureMessage.Decode(response.Payload.Span);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldBe($"{code}: {original.Message}");
        failure.Diagnostics.Select(Value).ShouldBe(original.Diagnostics.Select(Value));
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldBe(original.Message);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Location.ShouldBe(oql ? DiagnosticLocation.Absolute : null);
        diagnostic.Start.ShouldBe(oql ? 7 : null);
        diagnostic.End.ShouldBe(oql ? statement.Length - 1 : null);
        diagnostic.Line.ShouldBe(oql ? 2 : 1);
        await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage("CREATE COLLECTION reusable", "{}"u8.ToArray()).Encode());
        var complete = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        complete.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        DocumentProtocolCompleteMessage.Decode(complete.Payload.Span).AffectedCount.ShouldBe(0);
        server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        (await embedded.GetCollectionAsync("items", token)).Name.ShouldBe("items");
        (await embedded.GetCollectionAsync("reusable", token)).Name.ShouldBe("reusable");

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), token);
            await channel.Writer.FlushAsync(token);
        }
    }

    private static object Value(Diagnostic diagnostic) =>
        (diagnostic.Code, diagnostic.Message, diagnostic.Start, diagnostic.End, diagnostic.Line, diagnostic.Severity, diagnostic.Location);
}
