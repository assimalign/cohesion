using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphDatabaseEventSourceCollection))]
public sealed class GraphOperationOutcomeTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Wire: streaming disposal distinguishes result failures from disposed transport")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_StreamingDisposal_ShouldClassifySourceAndEndTransaction(bool transport)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var serverSession = (GraphDatabaseServerSession)harness.Server.Sessions.Single();
        var session = serverSession.DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (:Pending)", cancellationToken: timeout.Token);
        int streams = 0;
        Action fail = () =>
        {
            if (!transport) { serverSession.BeforeResultWrite = null; }
            streams++;
            throw new ObjectDisposedException(transport ? "protocol channel" : "result materializer");
        };
        if (transport) { serverSession.BeforeTransportWrite = fail; }
        else { serverSession.BeforeResultWrite = fail; }

        // Act
        await client.SendAsync(GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create("SHOW LABELS").Encode());
        if (transport)
        {
            (await client.ReadAsync()).ShouldBeNull();
            await serverSession.Completion.WaitAsync(timeout.Token);
        }
        else
        {
            var frame = await client.ExpectAsync(ProtocolMessageType.Error);
            ProtocolErrorMessage.Decode(frame.Payload.Span).Message.ShouldStartWith("COHDBG019:", Case.Sensitive);
            transaction.State.ShouldBe(TransactionState.Faulted);
            await client.SendAsync(ProtocolMessageType.Ping);
            await client.ExpectAsync(ProtocolMessageType.Pong);
            await transaction.RollbackAsync(timeout.Token);
        }

        // Assert
        streams.ShouldBe(1);
        if (transport)
        {
            serverSession.CloseReason.ShouldBe(GraphDatabaseEventSource.CloseReason.Faulted);
            recorder.Events.Where(item => item.EventId == 7).ShouldHaveSingleItem();
        }
        else { recorder.Events.ShouldNotContain(item => item.EventId == 7); }
        await using var observer = await session.Database.CreateSessionAsync(timeout.Token);
        (await GraphCatalogIntrospectionTests.Rows(observer, "SHOW LABELS")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Transaction: closing before the kernel end reports structured COHDBG019")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndAsync_CoordinatorClosesBeforeKernelEnd_ShouldReportDisposed(bool commit)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("closing-end", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Pending)", cancellationToken: CancellationToken.None);
        int closes = 0;
        database.BeforeTransactionEndAsync = async () =>
        {
            database.BeforeTransactionEndAsync = null;
            closes++;
            await database.Coordinator.DisposeAsync();
        };

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (commit) { await transaction.CommitAsync(CancellationToken.None); }
            else { await transaction.RollbackAsync(CancellationToken.None); }
        });

        // Assert
        closes.ShouldBe(1);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG019");
        failure.Message.ShouldNotStartWith("COHDBG019:", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<ObjectDisposedException>();
        transaction.State.ShouldNotBe(TransactionState.Committed);
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test", CancellationToken.None);
        await using var observer = await reopened.CreateSessionAsync(CancellationToken.None);
        (await GraphCatalogIntrospectionTests.Rows(observer, "SHOW LABELS")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Autocommit: snapshot cleanup cannot replace a durable success")]
    public async Task ExecuteAsync_PinReleaseDisposedAfterCommit_ShouldSucceedAndRetainDurableRow()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true) { LoseUnconfirmedJournalOnReopen = true };
        await using var engine = GraphDatabaseEngine.Create("pin-outcome", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("test", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        int releases = 0;
        database.BeforeSnapshotPinRelease = pin =>
        {
            pin.State.ShouldBe(TransactionState.Active);
            releases++;
            throw new ObjectDisposedException("snapshot pin");
        };

        // Act
        var result = await session.ExecuteAsync("INSERT (:Durable {value: 42})", cancellationToken: timeout.Token);
        database.BeforeSnapshotPinRelease = null;
        await session.DisposeAsync();
        await database.DisposeAsync();
        var reopened = await engine.OpenDatabaseAsync("test", timeout.Token);
        await using var observer = await reopened.CreateSessionAsync(timeout.Token);
        var rows = await GraphCatalogIntrospectionTests.Rows(observer, "MATCH (n:Durable) RETURN n.value");

        // Assert
        releases.ShouldBe(1);
        result.AffectedCount.ShouldBe(1);
        rows.ShouldHaveSingleItem()[0].ShouldBe(42L);
        var cleanup = recorder.Events.Where(item => item.EventId == 13).ShouldHaveSingleItem();
        cleanup.PayloadNames.ShouldBe(["database", "exceptionType"]);
        cleanup.Payload.ShouldBe(["test", typeof(ObjectDisposedException).FullName]);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }
}
