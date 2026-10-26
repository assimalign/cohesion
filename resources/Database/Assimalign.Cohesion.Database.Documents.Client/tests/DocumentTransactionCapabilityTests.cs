using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

[Collection(nameof(DocumentClientEventSourceCollection))]
public sealed class DocumentTransactionCapabilityTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Unnegotiated controls cannot leak transactions across pooled rentals (#1725)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_UnnegotiatedControl_ShouldRefuseAndPreserveIdlePooledSession(bool tcp, bool failedStatement)
    {
        // Arrange: reproduce the capability-free Documents family used by 2.0/2.1 drivers.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        var family = new ProtocolMessageFamily("Documents", 64, 65, 66, 67, 68, 69, 70, 71, 72);
        await using var client = DatabaseClient.Create(new()
        {
            Family = family,
            Settings = new DatabaseConnectionSettings { Database = "documents", Principal = "legacy", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.Factory,
        });
        await using var first = await client.RentAsync(harness.Token);
        await first.ExecuteAsync(new Exchange(family, "CREATE COLLECTION items"), harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem();

        // Act / Assert: every recognized spelling refuses before opening or touching a transaction.
        foreach (string control in new[] { "BEGIN", "BEGIN WORK", "BEGIN TRAN", "BEGIN TRANSACTION ISOLATION LEVEL SNAPSHOT",
            "START WORK", "START TRAN", "START TRANSACTION ISOLATION LEVEL READ COMMITTED", "COMMIT", "COMMIT WORK",
            "COMMIT TRAN", "COMMIT TRANSACTION", "ROLLBACK", "ROLLBACK WORK", "ROLLBACK TRAN", "ROLLBACK TRANSACTION" })
        {
            var refusal = await first.ExecuteAsync(new Exchange(family, control), harness.Token);
            refusal.ShouldNotBeNull().Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD07002");
            refusal.Message.ShouldBe("CD07002: Text transaction control requires the negotiated transaction-status capability.");
            session.DatabaseSession!.CurrentTransaction.ShouldBeNull();
        }
        (await first.ExecuteAsync(new Exchange(family, "INSERT INTO items (KEY, VALUE) VALUES ('first', {})"), harness.Token)).ShouldBeNull();
        if (failedStatement)
        {
            var failure = await first.ExecuteAsync(new Exchange(family, "SELECT * FROM absent"), harness.Token);
            failure.ShouldNotBeNull().Diagnostics.ShouldContain(item => item.Code == "CD01002");
        }
        await first.DisposeAsync();
        await using var next = await client.RentAsync(harness.Token);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session.Id);
        session.DatabaseSession!.CurrentTransaction.ShouldBeNull();
        (await next.ExecuteAsync(new Exchange(family, "INSERT INTO items (KEY, VALUE) VALUES ('next', {})"), harness.Token)).ShouldBeNull();
        await using var observer = await harness.Database.CreateSessionAsync(harness.Token);
        var items = await observer.GetCollectionAsync("items", harness.Token);
        (await items.GetAsync(observer, "first", harness.Token)).ShouldNotBeNull();
        (await items.GetAsync(observer, "next", harness.Token)).ShouldNotBeNull();
    }

    private sealed class Exchange : DatabaseProtocolExchange<DocumentProtocolFailureMessage?>
    {
        private readonly string _statement;

        internal Exchange(ProtocolMessageFamily family, string statement) : base(family) => _statement = statement;

        protected override async ValueTask<DocumentProtocolFailureMessage?> ExecuteCoreAsync(ProtocolFrameReader reader,
            ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new((ProtocolMessageType)64, DocumentProtocolExecuteMessage.Create(_statement).Encode()), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            var frame = (await reader.ReadFrameAsync(cancellationToken)).ShouldNotBeNull();
            if ((byte)frame.Type == 72) { return DocumentProtocolFailureMessage.Decode(frame.Payload.Span); }
            frame.Type.ShouldBe((ProtocolMessageType)66);
            DocumentProtocolCompleteMessage.Decode(frame.Payload.Span).ResultCount.ShouldBe(0);
            return null;
        }
    }
}
