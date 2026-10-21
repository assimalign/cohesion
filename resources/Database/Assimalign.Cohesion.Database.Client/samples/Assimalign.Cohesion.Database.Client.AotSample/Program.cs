using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Blob.Client;
using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.Documents.Client;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Graph.Client;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.KeyValuePair.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Types;

using SqlWire = Assimalign.Cohesion.Database.Sql.Protocol;
using KeyValueWire = Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.Client.AotSample;

// This executable composes transport and remote clients only. No server or engine
// participates, even in its pre-trim compiler reference closure.
internal static class Program
{
    private static async Task Main()
    {
        await using var listener = new InMemoryConnectionListener();
        var factory = listener.CreateFactory();
        var settings = new DatabaseConnectionSettings { Database = "canary", EndPoint = listener.EndPoint };
        await using var sql = SqlClient.Create(new() { Settings = settings, ConnectionFactory = factory });
        await using var graph = GraphClient.Create(new() { Settings = settings, ConnectionFactory = factory });
        await using var documents = DocumentClient.Create(new() { Settings = settings, ConnectionFactory = factory });
        await using var keyValue = KeyValueClient.Create(new() { Settings = settings, ConnectionFactory = factory });
        await using var blob = BlobClient.Create(new() { Settings = settings, ConnectionFactory = factory });

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await CanceledAsync(async () => { await using var connection = await sql.ConnectAsync(cancellation.Token); });
        await CanceledAsync(async () => { await using var connection = await graph.ConnectAsync(cancellation.Token); });
        await CanceledAsync(async () => { await using var connection = await documents.ConnectAsync(cancellation.Token); });
        await CanceledAsync(async () => { await using var connection = await keyValue.ConnectAsync(cancellation.Token); });
        await CanceledAsync(async () => { await using var connection = await blob.ConnectAsync(cancellation.Token); });

        Check(SqlWire.ProtocolExecuteMessage.Decode(SqlWire.ProtocolExecuteMessage.Create("SELECT 1").Encode()).Statement == "SELECT 1");
        Check(SqlWire.ProtocolResultCompleteMessage.Decode(new SqlWire.ProtocolResultCompleteMessage(7).Encode()).AffectedCount == 7);
        var command = new KeyValueWire.ProtocolExecuteMessage("GET k", new Dictionary<string, byte[]>());
        Check(KeyValueWire.ProtocolExecuteMessage.Decode(command.Encode()).Statement == "GET k");
        Check(KeyValueWire.ProtocolResultCompleteMessage.Decode(new KeyValueWire.ProtocolResultCompleteMessage(3).Encode()).AffectedCount == 3);
        Check(GraphValueCodec.Decode(GraphValueCodec.Encode(new object?[] { 1.20m, "value" })) is object?[] { Length: 2 });
        var document = new Document(new DocumentId("one"), new DocumentVersion(1), "{}"u8.ToArray());
        Check(DocumentProtocolStoredDocumentMessage.Decode(new DocumentProtocolStoredDocumentMessage(document).Encode()).Document.Id == document.Id);
        Check(BlobWriteMessage.Decode(new BlobWriteMessage("files", "one").Encode()).Name == "one");
        Check(BlobChunkMessage.Decode(new BlobChunkMessage(new byte[] { 1, 2 }).ToFrame().Payload).Content.Length == 2);

        var wire = new[] { new ProtocolDiagnostic(ProtocolDiagnosticSeverity.Warning, "CANARY", "text", 1, 2, 1) };
        var shared = new DatabaseClientException(ProtocolErrorCode.ExecutionFailure, "shared", wire, null);
        var sqlError = new SqlClientException(SqlClientErrorKind.ExecutionFailure, shared.Code, "sql", shared);
        var graphError = new GraphClientException(shared.Code, "graph", shared);
        var documentError = new DocumentClientException(shared.Code, "documents", shared, shared.Diagnostics);
        Check(sqlError.Diagnostics.Count == 1 && sqlError.ConnectionUsable);
        Check(graphError.Diagnostics[0].Location == DiagnosticLocation.Absolute);
        Check(documentError.Diagnostics[0].Severity == DiagnosticSeverity.Warning);
        Check(new KeyValueClientException(KeyValueClientErrorKind.ExecutionFailure, shared.Code, "kv", shared).Diagnostics.Count == 0);
        Check(new BlobClientException(shared.Code, "blob", shared).Diagnostics.Count == 0);
        using var pathTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await RunPathExchangeAsync(graph, listener, pathTimeout.Token);
        Console.WriteLine("Client-only NativeAOT canary passed: five clients, five wire codecs, six diagnostic exception paths and twenty batched graph paths.");
    }

    private static async Task RunPathExchangeAsync(GraphClient graph, InMemoryConnectionListener listener,
        CancellationToken cancellationToken = default)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task peer = ServePathsAsync(listener, finished.Task, cancellationToken);
        try
        {
            await using var connection = await graph.ConnectAsync(cancellationToken);
            GraphPath? retained = null;
            int count = 0;
            await foreach (var path in connection.QueryPathsAsync("MATCH (n) RETURN n", cancellationToken: cancellationToken))
            {
                retained ??= path;
                Check(path.Nodes.Count == 1 && path.Nodes[0].Id.Value == 1 && path.Relationships.Count == 0);
                count++;
            }
            Check(count == 20 && Equals(retained!.Nodes[0].Properties["name"], "canary"));
        }
        finally
        {
            // Keep the peer alive through terminal verification, and join it on every exit.
            finished.TrySetResult();
            await peer;
        }
    }

    private static async Task ServePathsAsync(InMemoryConnectionListener listener, Task finished,
        CancellationToken cancellationToken = default)
    {
        await using var transport = await listener.AcceptAsync(cancellationToken);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family, batchWrites: true);
        var startupFrame = await channel.Reader.ReadBorrowedFrameAsync(cancellationToken);
        Check(startupFrame?.Type == ProtocolMessageType.Startup);
        var startup = ProtocolStartupMessage.Decode(startupFrame!.Value.Payload.Span);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty), cancellationToken);
        await channel.Writer.FlushAsync(cancellationToken);
        Check((await channel.Reader.ReadBorrowedFrameAsync(cancellationToken))?.Type == ProtocolMessageType.AuthenticateResponse);
        var ready = channel.Negotiate(ProtocolVersion.Current, startup.Capabilities);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Ready, ready.Encode()), cancellationToken);
        await channel.Writer.FlushAsync(cancellationToken);
        Check((await channel.Reader.ReadBorrowedFrameAsync(cancellationToken))?.Type == (ProtocolMessageType)GraphProtocolMessageType.ExecutePaths);
        var node = new GraphNode(new(1), ["Canary"], new Dictionary<string, object?> { ["name"] = "canary" });
        var message = new GraphProtocolPathMessage([node], []);
        byte[] payload = new byte[message.GetEncodedLength()];
        Check(message.Encode(payload) == payload.Length);
        for (int i = 0; i < 20; i++)
        {
            await channel.Writer.WriteFrameAsync(new((ProtocolMessageType)GraphProtocolMessageType.Path, payload), cancellationToken);
        }
        await channel.Writer.WriteFrameAsync(new((ProtocolMessageType)GraphProtocolMessageType.PathsComplete,
            new GraphProtocolPathsCompleteMessage(20).Encode()), cancellationToken);
        await channel.Writer.FlushAsync(cancellationToken);
        await finished.WaitAsync(cancellationToken);
    }

    private static async Task CanceledAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A canceled client dial completed.");
    }

    private static void Check(bool result)
    {
        if (!result) { throw new InvalidOperationException("Client-only wire or diagnostics check failed."); }
    }
}
