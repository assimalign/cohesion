using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphEngineFailureWireTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Engine: a disposed commit resource cannot hide a kernel transaction outcome")]
    public async Task TranslateKernelFailure_DisposedResource_ShouldRetainTransactionOutcome()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("commit-failure", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var disposed = new ObjectDisposedException("commit resource");
        var unconfirmed = new TransactionCommitUnconfirmedException("Commit durability is unknown.", disposed);
        var aborted = new TransactionAbortedException("The transaction was aborted.", disposed);

        // Act
        var uncertain = database.TranslateKernelFailure(unconfirmed).ShouldBeOfType<DatabaseTransactionCommitUnconfirmedException>();
        var refused = database.TranslateKernelFailure(aborted).ShouldBeOfType<DatabaseTransactionAbortedException>();

        // Assert
        uncertain.InnerException.ShouldBeSameAs(unconfirmed);
        refused.InnerException.ShouldBeSameAs(aborted);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Wire: child failures report execution failure and preserve the session")]
    [InlineData(0, false, "CG06006")]
    [InlineData(1, false, "CG06006")]
    [InlineData(2, false, "CG04007")]
    [InlineData(3, false, "CG09019")]
    [InlineData(4, false, "CK06003")]
    [InlineData(0, true, "CG06006")]
    [InlineData(1, true, "CG06006")]
    [InlineData(2, true, "CG04007")]
    [InlineData(3, true, "CG09019")]
    [InlineData(4, true, "CK06003")]
    public async Task Execute_EngineFailure_ShouldKeepSessionReady(int kind, bool explicitTransaction, string code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await GraphServerHarness.StartAsync();
        var database = harness.Engine.TryGetDatabase(GraphServerHarness.DatabaseName, out var found) ? found! : throw new InvalidOperationException();
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (:Seed)", cancellationToken: timeout.Token);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var wireSession = (GraphDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        await using var transaction = explicitTransaction ? await wireSession.BeginTransactionAsync(timeout.Token) : null;
        Exception cause = kind switch
        {
            0 => new IndexException("Injected index read failure."),
            1 => new GraphCatalogException("Injected malformed catalog record."),
            2 => GraphCatalog.Conflict(),
            4 => new IndexCorruptionException("graph index", 42, 0),
            _ => new ObjectDisposedException("graph storage"),
        };
        int visits = 0;
        database.Catalog.BeforeRecordRead = () =>
        {
            database.Catalog.BeforeRecordRead = null;
            visits++;
            throw cause;
        };

        // Act
        await client.SendAsync(GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create("SHOW LABELS").Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var failure = ProtocolErrorMessage.Decode(frame.Payload.Span);

        // Assert
        visits.ShouldBe(1);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith(code + ":", Case.Sensitive);
        if (explicitTransaction)
        {
            await client.SendAsync(GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create("INSERT (:Refused)").Encode());
            var refused = await client.ExpectAsync(ProtocolMessageType.Error);
            var refusal = ProtocolErrorMessage.Decode(refused.Payload.Span);
            refusal.Message.ShouldStartWith("CG04007:", Case.Sensitive);
            await transaction!.RollbackAsync(timeout.Token);
        }
        await client.SendAsync(ProtocolMessageType.Ping);
        await client.ExpectAsync(ProtocolMessageType.Pong);
        await client.SendAsync(GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create("INSERT (:After)").Encode());
        await client.ExpectAsync((ProtocolMessageType)GraphProtocolMessageType.ResultComplete);
        (await GraphSchema.Open(database, session).GetLabelsAsync(timeout.Token)).Select(item => item.Name).ShouldBe(["After", "Seed"]);
        harness.Server.Sessions.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Wire: a disposed database reports a coded failure and answers ping")]
    public async Task Execute_DisposedDatabase_ShouldReportDisposalAndKeepSessionReady()
    {
        // Arrange
        await using var harness = await GraphServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var session = (GraphDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        await session.Database.DisposeAsync();

        // Act
        await client.SendAsync(GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create("SHOW LABELS").Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith("CG09019:", Case.Sensitive);
        await client.SendAsync(ProtocolMessageType.Ping);
        await client.ExpectAsync(ProtocolMessageType.Pong);
        harness.Server.Sessions.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Engine: catalog conflicts, corruption and disposal retain their causes")]
    public async Task TranslateFailure_CatalogAndDisposal_ShouldClassifyAndRetainCause()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("failure-translation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var conflict = GraphCatalog.Conflict();
        var malformed = new GraphCatalogException("Malformed metadata.");
        var disposed = new ObjectDisposedException("resource");

        // Act
        var retry = database.TranslateFailure(conflict).ShouldBeOfType<DatabaseTransactionAbortedException>();
        var corruption = database.TranslateFailure(new Exception("Wrapped metadata.", malformed)).ShouldBeOfType<DatabaseException>();
        var lifetime = database.TranslateFailure(disposed).ShouldBeOfType<DatabaseException>();

        // Assert
        retry.InnerException.ShouldBeSameAs(conflict);
        retry.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
        retry.Message.ShouldBe(conflict.Message);
        corruption.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06006");
        corruption.InnerException!.InnerException.ShouldBeSameAs(malformed);
        lifetime.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG09019");
        lifetime.InnerException.ShouldBeSameAs(disposed);
        database.TranslateFailure(lifetime).ShouldBeSameAs(lifetime);
        var mismatch = database.TranslateFailure(new InvalidOperationException("Data mismatch.")).ShouldBeOfType<DatabaseException>();
        mismatch.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        mismatch.Message.ShouldBe("Data mismatch.");
        var mixed = database.TranslateFailure(new AggregateException(conflict, new IndexException("Index failed during cleanup.")))
            .ShouldBeOfType<DatabaseException>();
        mixed.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06006");
        var storageAndDisposal = database.TranslateFailure(new AggregateException(disposed, new IndexException("Index failed.")))
            .ShouldBeOfType<DatabaseException>();
        storageAndDisposal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06006");
    }
}
