using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentReturningWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - RETURNING: UPDATE and DELETE see earlier writes in their explicit transaction")]
    [InlineData(false, "UPDATE")]
    [InlineData(true, "UPDATE")]
    [InlineData(false, "DELETE")]
    [InlineData(true, "DELETE")]
    public async Task QueryAsync_ReturningOwnWrite_ShouldUseTransactionImage(bool tcp, string verb)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('prior', {n:1})", cancellationToken: harness.Token);
        var inserted = (await connection.GetAsync("items", "prior", harness.Token)).ShouldNotBeNull();

        // Act
        var result = await connection.QueryAsync((verb == "UPDATE" ? "UPDATE items SET n = n + 10" : "DELETE FROM items") +
            " RETURNING META().id AS id, META().version AS version, n", cancellationToken: harness.Token);

        // Assert
        result.AffectedCount.ShouldBe(1);
        var row = result.ShouldHaveSingleItem();
        row.GetString(0).ShouldBe("prior");
        row[2].ShouldBe(verb == "UPDATE" ? 11m : 1m);
        if (verb == "DELETE") { row[1].ShouldBe((decimal)inserted.Version.Value); }
        else
        {
            var updated = (await connection.GetAsync("items", "prior", harness.Token)).ShouldNotBeNull();
            row[1].ShouldBe((decimal)updated.Version.Value);
            updated.Version.ShouldNotBe(inserted.Version);
        }
        await connection.ExecuteAsync("COMMIT", cancellationToken: harness.Token);
        (await connection.GetAsync("items", "prior", harness.Token)).HasValue.ShouldBe(verb == "UPDATE");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - RETURNING: legacy completion rejection follows autocommit but disconnect rolls back explicit work")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueryAsync_LegacyCompletionRejected_ShouldPreserveCommitBoundary(bool tcp, bool explicitTransaction)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var observer = await harness.Client.ConnectAsync(harness.Token);
        await observer.CreateCollectionAsync("items", harness.Token);
        await observer.PutAsync("items", "a", "{\"n\":1}"u8.ToArray(), cancellationToken: harness.Token);
        await using var legacy = DatabaseClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "documents", Principal = "legacy", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Factory,
            Family = DocumentProtocol.Family,
        });
        await using var connection = await legacy.RentAsync(harness.Token);
        var serverSession = harness.Server.Sessions.Single(session => session.Principal == "legacy");
        if (explicitTransaction) { await connection.ExecuteAsync(new LegacyDocumentExchange("BEGIN"), harness.Token); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseClientException>(async () => await connection.ExecuteAsync(
            new LegacyDocumentExchange("UPDATE items SET n = n + 10 RETURNING n"), harness.Token));

        // Assert: join that closed session's cleanup before reading the rollback outcome.
        failure.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        connection.IsOpen.ShouldBeFalse();
        await serverSession.DisposeAsync().AsTask().WaitAsync(harness.Token);
        var observed = await observer.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token);
        observed.ShouldHaveSingleItem()[0].ShouldBe(explicitTransaction ? 1m : 11m);
        if (!explicitTransaction)
        {
            var retried = await observer.QueryAsync("UPDATE items SET n = n + 10 RETURNING n", cancellationToken: harness.Token);
            retried.ShouldHaveSingleItem()[0].ShouldBe(21m);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - RETURNING: oversized serialized projections fail before any header and roll back writes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_ReturningRowExceedsFrame_ShouldRollbackBeforeResponse(bool tcp)
    {
        // Arrange: each escaped string fits the frame; the two named fields together do not.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await SeedAsync(connection, harness);
        var original = (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull();
        byte[] parameters = Encoding.UTF8.GetBytes("{\"large\":\"" +
            new string('\u0001', 1536 * 1024).Replace("\u0001", "\\u0001", StringComparison.Ordinal) + "\"}");
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);

        // Act: the small first image projects successfully, and the next serialized image overflows.
        await SendAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute, new DocumentProtocolExecuteMessage(
            "UPDATE items SET marker = 1 RETURNING CASE WHEN n = 1 THEN 'small' ELSE $large END AS a, " +
            "CASE WHEN n = 1 THEN 'small' ELSE $large END AS b", parameters).Encode());

        // Assert
        var first = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        first.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
        DocumentProtocolFailureMessage.Decode(first.Payload.Span).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05016");
        (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull().Version.ShouldBe(original.Version);
        var row = await connection.QueryAsync("SELECT marker FROM items WHERE META().id = 'b'", cancellationToken: harness.Token);
        row.ShouldHaveSingleItem().IsMissing(0).ShouldBeTrue();

        async Task SendAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - RETURNING: all writes carry owned images, metadata and affected counts on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ReturningWrites_ShouldPreserveImagesAndCounts(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        foreach (string verb in new[] { "INSERT", "UPSERT", "UPDATE", "DELETE" })
        {
            await SeedAsync(connection, harness);
            var beforeA = (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull();
            var beforeB = (await connection.GetAsync("items", "b", harness.Token)).ShouldNotBeNull();

            // Act
            var result = await connection.QueryAsync(Write(verb) +
                " RETURNING META(d).id AS id, META(d).version AS version, d.n AS n, d.nested AS nested, d.absent AS absent, NULL AS null_value",
                cancellationToken: harness.Token);

            // Assert
            result.AffectedCount.ShouldBe(2);
            result.Count.ShouldBe(2);
            result.IsDocumentQuery.ShouldBeFalse();
            result.Columns.Select(column => column.Name).ShouldBe(["id", "version", "n", "nested", "absent", "null_value"]);
            result.Columns[1].Type.ShouldBe(DatabaseType.Decimal);
            for (int index = 0; index < result.Count; index++)
            {
                var row = result[index];
                string id = row.GetString(0)!;
                var stored = verb == "DELETE" ? index == 0 ? beforeA : beforeB :
                    (await connection.GetAsync("items", new DocumentId(id), harness.Token)).ShouldNotBeNull();
                row[1].ShouldBe((decimal)stored.Version.Value);
                using var json = JsonDocument.Parse(stored.Content);
                row[2].ShouldBe(json.RootElement.GetProperty("n").GetDecimal());
                JsonElement.DeepEquals(row[3].ShouldBeOfType<JsonElement>(), json.RootElement.GetProperty("nested")).ShouldBeTrue();
                row.IsMissing(4).ShouldBeTrue();
                row.IsNull(4).ShouldBeFalse();
                row.IsNull(5).ShouldBeTrue();
            }
            await connection.ExecuteAsync("DELETE FROM items", cancellationToken: harness.Token);
            result[0][3].ShouldBeOfType<JsonElement>()[0].GetInt32().ShouldBe(verb == "UPSERT" ? 3 : 1);
        }

        // Bare projections use the same API and include zero-row metadata.
        foreach (string verb in new[] { "INSERT", "UPSERT", "UPDATE", "DELETE" })
        {
            foreach (string modifier in new[] { "RAW", "VALUE" })
            {
                await SeedAsync(connection, harness);
                var bare = await connection.QueryAsync(Write(verb) + " RETURNING " + modifier + " [d.n, MISSING]", cancellationToken: harness.Token);
                bare.AffectedCount.ShouldBe(2);
                bare.IsDocumentQuery.ShouldBeTrue();
                bare.Columns.ShouldHaveSingleItem().Name.ShouldBe("value");
                bare.Documents.Count.ShouldBe(2);
                bare.Documents[0][0].GetInt32().ShouldBe(verb == "UPDATE" ? 11 : 1);
                bare.Documents[0][1].ValueKind.ShouldBe(JsonValueKind.Null);
                await connection.ExecuteAsync("DELETE FROM items", cancellationToken: harness.Token);
                var empty = await connection.QueryAsync("DELETE FROM items RETURNING VALUE LENGTH('x')", cancellationToken: harness.Token);
                empty.AffectedCount.ShouldBe(0);
                empty.IsDocumentQuery.ShouldBeTrue();
                empty.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
                empty.ShouldBeEmpty();
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - RETURNING: the existing Complete carries both counts and projection failures emit no partial rows")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_ReturningFrames_ShouldCompleteBothCountsOrFailBeforeHeader(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await SeedAsync(connection, harness);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);

        foreach (string verb in new[] { "INSERT", "UPSERT", "UPDATE", "DELETE" })
        {
            // Act: the first image projects successfully; division by zero in the second rolls back.
            string write = verb == "UPDATE" ? "UPDATE items d SET n = n" : Write(verb);
            await ExecuteAsync(write + " RETURNING 10 / (d.n - 2) AS quotient");

            // Assert: a Failure is the very first response, with no header or row preceding it.
            var failedFrame = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            failedFrame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
            var failure = DocumentProtocolFailureMessage.Decode(failedFrame.Payload.Span);
            failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
            (await connection.GetAsync("items", "c", harness.Token)).ShouldBeNull();
            (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull();
        }

        await ExecuteAsync("UPDATE items SET n = n + 10 RETURNING n");
        var header = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        header.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.ResultHeader);
        DocumentProtocolResultHeaderMessage.Decode(header.Payload.Span).WholeDocuments.ShouldBeFalse();
        foreach (int number in new[] { 11, 12 })
        {
            var row = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            row.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Document);
            Encoding.UTF8.GetString(DocumentProtocolResultMessage.Decode(row.Payload.Span).Json.Span).ShouldBe("{\"n\":" + number + "}");
        }
        var complete = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
        complete.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        complete.Payload.Length.ShouldBe(16);
        var counts = DocumentProtocolCompleteMessage.Decode(complete.Payload.Span);
        counts.ResultCount.ShouldBe(2);
        counts.AffectedCount.ShouldBe(2);

        async Task ExecuteAsync(string query) => await SendAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage(query, "{}"u8.ToArray()).Encode());

        async Task SendAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), harness.Token);
            await channel.Writer.FlushAsync(harness.Token);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - RETURNING: failed explicit work and budget exhaustion roll back and preserve the connection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ReturningFailure_ShouldRollbackAndKeepSession(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp,
            configureEngine: builder => builder.Options.StatementMemoryBudget = 1000);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await SeedAsync(connection, harness);
        var original = (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull();
        foreach (string verb in new[] { "INSERT", "UPSERT", "UPDATE", "DELETE" })
        {
            // Act
            var failed = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(
                Write(verb) + " RETURNING $payload AS value", new Dictionary<string, object?> { ["payload"] = new string('x', 200) }, harness.Token));

            // Assert
            failed.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            connection.IsOpen.ShouldBeTrue();
            (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull().Version.ShouldBe(original.Version);
            (await connection.GetAsync("items", "c", harness.Token)).ShouldBeNull();
        }
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('prior', 9)", cancellationToken: harness.Token);
        var projection = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(
            "DELETE FROM items d WHERE n < 3 RETURNING 10 / (n - 2)", cancellationToken: harness.Token));
        projection.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
        var refused = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT * FROM items", cancellationToken: harness.Token));
        refused.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04101");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.GetAsync("items", "prior", harness.Token)).ShouldBeNull();
        (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull().Version.ShouldBe(original.Version);
        connection.IsOpen.ShouldBeTrue();
    }

    private static async Task SeedAsync(DocumentConnection connection, DocumentClientTestHarness harness)
    {
        await connection.ExecuteAsync("DELETE FROM items", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1,nested:[1]}), ('b', {n:2,nested:[2]})", cancellationToken: harness.Token);
    }

    private static string Write(string verb) => verb switch
    {
        "INSERT" => "INSERT INTO items AS d (KEY, VALUE) VALUES ('c', {n:1,nested:[1]}), ('d', {n:2,nested:[2]})",
        "UPSERT" => "UPSERT INTO items AS d (KEY, VALUE) VALUES ('a', {n:1,nested:[3]}), ('b', {n:2,nested:[4]})",
        "UPDATE" => "UPDATE items d SET n = n + 10",
        _ => "DELETE FROM items d",
    };
}
