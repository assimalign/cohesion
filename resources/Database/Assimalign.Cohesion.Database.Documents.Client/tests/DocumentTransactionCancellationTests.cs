using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

[Collection(nameof(DocumentClientEventSourceCollection))]
public sealed class DocumentTransactionCancellationTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Lost COMMIT completion or status reports failure, discards the connection and never retries (#1725)")]
    [InlineData(false, "complete")]
    [InlineData(true, "complete")]
    [InlineData(false, "ready-close")]
    [InlineData(true, "ready-close")]
    [InlineData(false, "ready-timeout")]
    [InlineData(true, "ready-timeout")]
    public async Task ExecuteAsync_LostCommitReply_ShouldFailDiscardAndNeverRetransmit(bool tcp, string drop)
    {
        // Arrange: both relay legs use the selected transport and a real server applies the writes.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var relay = await DocumentCommitRelay.StartAsync(harness, tcp, drop);
        await using var client = DocumentClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "documents", Principal = "relayed", EndPoint = relay.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = relay.Factory,
        });
        await using var connection = await client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('first', {}), ('second', {})", cancellationToken: harness.Token);
        await using var observer = await harness.Database.CreateSessionAsync(harness.Token);
        var items = await observer.GetCollectionAsync("items", harness.Token);
        (await items.GetAsync(observer, "first", harness.Token)).ShouldBeNull();
        (await items.GetAsync(observer, "second", harness.Token)).ShouldBeNull();

        // Act: the relay drops the frame before the typed client can receive it.
        if (drop == "ready-timeout")
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
                await connection.ExecuteAsync("COMMIT", cancellationToken: harness.Token));
            error.CancellationToken.ShouldNotBe(harness.Token);
            harness.Token.IsCancellationRequested.ShouldBeFalse();
        }
        else
        {
            var error = await Should.ThrowAsync<DocumentClientException>(async () =>
                await connection.ExecuteAsync("COMMIT", cancellationToken: harness.Token));
            error.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        }
        await connection.DisposeAsync();
        await using var next = await client.ConnectAsync(harness.Token);
        (await next.GetCollectionsAsync(harness.Token)).ShouldBe(["items"]);

        // Assert: a distinct physical connection, exactly one COMMIT, and atomic independent visibility.
        relay.Connections.ShouldBe(2);
        relay.Commits.ShouldBe(1);
        relay.Drops.ShouldBe(1);
        relay.Completions.ShouldBe(drop == "complete" ? 0 : 1);
        // Consuming a real Complete at the relay proves this transaction committed, even if its caller cannot know.
        (await items.GetAsync(observer, "first", harness.Token)).ShouldNotBeNull();
        (await items.GetAsync(observer, "second", harness.Token)).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Disconnect without reading COMMIT completion cannot undo a committed transaction (#1725)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_UnreadCommitCompletion_ShouldRetainCommittedWrites(bool tcp)
    {
        // Arrange: bypass typed acknowledgement handling so the completion remains unread.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester")
            { Capabilities = [ProtocolCapability.TransactionStatus.Name] }.Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        await ExecuteAsync("CREATE COLLECTION items");
        await ExecuteAsync("BEGIN");
        await ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('committed', {})");
        var transaction = harness.Server.Sessions.Single().DatabaseSession!.CurrentTransaction.ShouldNotBeNull();

        // Act: recorded commit state, rather than a delay, selects the lost-response window.
        await WriteAsync((ProtocolMessageType)64, DocumentProtocolExecuteMessage.Create("COMMIT").Encode());
        while (transaction.State != TransactionState.Committed) { await Task.Delay(1, harness.Token); }
        await transport.DisposeAsync();

        // Assert: a new session sees the commit although the caller received no completion.
        await using var observer = await harness.Database.CreateSessionAsync(harness.Token);
        var collection = await observer.GetCollectionAsync("items", harness.Token);
        (await collection.GetAsync(observer, "committed", harness.Token)).ShouldNotBeNull();

        async Task ExecuteAsync(string statement)
        {
            await WriteAsync((ProtocolMessageType)64, DocumentProtocolExecuteMessage.Create(statement).Encode());
            (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)66);
            (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        }

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Cancellation and disposal races discard pending transactions and release every pool slot (#1725)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_RacingCanceledTransactionOperation_ShouldReleaseWriterAndPoolSlot(bool tcp)
    {
        // Arrange: the function records entry before waiting on the server's session token.
        // Minimum duration exercises scheduling; counters, not elapsed time, decide success.
        var pause = new PausedFunction();
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp,
            configureEngine: builder => builder.Functions.Add(pause));
        await using (var setup = await harness.Client.ConnectAsync(harness.Token))
        { await setup.CreateCollectionAsync("items", harness.Token); }
        var clock = Stopwatch.StartNew();
        int canceled = 0;
        int disposed = 0;
        int aborted = 0;
        int overlapping = 0;

        // Act
        do
        {
            harness.Token.ThrowIfCancellationRequested();
            await using var connection = await harness.Client.ConnectAsync(harness.Token);
            await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
            await connection.PutAsync("items", "pending", "{}"u8.ToArray(), cancellationToken: harness.Token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.Token);
            pause.Prepare();
            Task<DocumentResultSet> query = connection.QueryAsync("SELECT pause_value(1) FROM items", cancellationToken: cancellation.Token).AsTask();
            await pause.Entered.WaitAsync(harness.Token);
            await Should.ThrowAsync<InvalidOperationException>(async () => await connection.GetCollectionsAsync(harness.Token));
            overlapping++;
            int mode = overlapping % 3;
            Task ending;
            if (mode == 0)
            {
                await cancellation.CancelAsync();
                canceled++;
                ending = connection.DisposeAsync().AsTask();
            }
            else if (mode == 1)
            {
                ending = connection.DisposeAsync().AsTask();
                disposed++;
            }
            else
            {
                ending = connection.AbortAsync().AsTask();
                aborted++;
            }
            await Should.ThrowAsync<OperationCanceledException>(async () => await query);
            await Task.WhenAll(ending, connection.DisposeAsync().AsTask(), connection.AbortAsync().AsTask()).WaitAsync(harness.Token);
            await using var next = await harness.Client.ConnectAsync(harness.Token);
            // This write must wait for server teardown to cancel/join the callback and undo.
            await next.PutAsync("items", "clean", "{}"u8.ToArray(), cancellationToken: harness.Token);
            (await next.GetAsync("items", "pending", harness.Token)).ShouldBeNull();
            if (clock.Elapsed > TimeSpan.FromSeconds(30)) { break; }
        }
        while (clock.Elapsed < TimeSpan.FromSeconds(1) || canceled < 10 || disposed < 10 || aborted < 10);

        // Assert: every admitted callback exited, every path returned a slot, and all races ran.
        canceled.ShouldBeGreaterThanOrEqualTo(10);
        disposed.ShouldBeGreaterThanOrEqualTo(10);
        aborted.ShouldBeGreaterThanOrEqualTo(10);
        overlapping.ShouldBeGreaterThanOrEqualTo(30);
        pause.Starts.ShouldBe(overlapping);
        pause.Exits.ShouldBe(overlapping);
    }

    private sealed class PausedFunction : DocumentScalarFunction
    {
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _starts;
        private int _exits;

        internal PausedFunction() : base("pause_value", 1, 1, DatabaseType.Decimal) { }
        internal Task Entered => _entered.Task;
        internal int Starts => Volatile.Read(ref _starts);
        internal int Exits => Volatile.Read(ref _exits);
        internal void Prepare() => _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override DocumentValue InvokeCore(scoped in DocumentArguments arguments)
        {
            Interlocked.Increment(ref _starts);
            _entered.TrySetResult();
            try
            {
                arguments.Context.CancellationToken.WaitHandle.WaitOne();
                arguments.Context.CancellationToken.ThrowIfCancellationRequested();
                return arguments[0];
            }
            finally { Interlocked.Increment(ref _exits); }
        }
    }
}
