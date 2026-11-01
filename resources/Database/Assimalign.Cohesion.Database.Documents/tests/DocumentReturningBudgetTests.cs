using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentReturningBudgetTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: default budget admits 5000 one KiB objects and one four MiB object")]
    [InlineData("INSERT", 5000, 1024)]
    [InlineData("DELETE", 5000, 1024)]
    [InlineData("INSERT", 1, 4 * 1024 * 1024)]
    [InlineData("DELETE", 1, 4 * 1024 * 1024)]
    public async Task ExecuteAsync_DefaultReturningBudget_ShouldAdmitDocumentSizedResults(string verb, int count, int size)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("budget", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        byte[] content = Content(size);
        await PrepareAsync(session, verb, count, content);

        // Act
        await using var result = (await session.ExecuteAsync(Statement(verb), cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.AffectedCount.ShouldBe(count);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.GetBytes(0).ToArray().ShouldBe(content);
            rows++;
        }
        rows.ShouldBe(count);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - RETURNING: exact JSON accounting admits the limit and one byte less rolls back")]
    [InlineData("INSERT", 1024, true)]
    [InlineData("DELETE", 1024, true)]
    [InlineData("INSERT", 2048, true)]
    [InlineData("DELETE", 2048, true)]
    [InlineData("UPDATE", 1024, true)]
    [InlineData("UPSERT", 1024, true)]
    [InlineData("UPDATE", 2048, true)]
    [InlineData("UPSERT", 2048, true)]
    [InlineData("INSERT", 1024, false)]
    [InlineData("DELETE", 1024, false)]
    [InlineData("INSERT", 2048, false)]
    [InlineData("DELETE", 2048, false)]
    [InlineData("UPDATE", 1024, false)]
    [InlineData("UPSERT", 1024, false)]
    [InlineData("UPDATE", 2048, false)]
    [InlineData("UPSERT", 2048, false)]
    public async Task ExecuteAsync_ExactReturningBudget_ShouldCountEachOwnedRepresentationOnce(string verb, int size, bool fits)
    {
        // Arrange: metadata = 208; row storage = 224; clone = 128 + UTF-8 + four 12-byte tokens;
        // original whole-image bytes are retained once alongside that independent clone.
        long perRow = 224 + 128 + size + 4 * 12 + size;
        // Pending buffers share the grant (#1755). IDs have five characters; INSERT/
        // UPSERT retain the key set and encoded bytes, UPDATE its patched bytes,
        // DELETE only identities. Pin the combined peak, still to the exact byte.
        long pendingPerRow = verb switch
        {
            "INSERT" or "UPSERT" => 192 + 5 * 2 + 32 + size,
            "UPDATE" => 160 + 5 * 2 + size,
            _ => 96 + 5 * 2,
        };
        long budget = 208 + 2 * (perRow + pendingPerRow) - (fits ? 0 : 1);
        await using var engine = DocumentDatabaseEngine.Create("budget", new() { StatementMemoryBudget = budget });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await PrepareAsync(session, verb, 2, Content(size));
        var target = (await session.GetCollectionAsync("items", CancellationToken.None)).ShouldNotBeNull();
        var original = await target.GetAsync(session, "00000", CancellationToken.None);

        // VALUES isolates pending/RETURNING ownership; SELECT staging has separate regressions.
        using var json = System.Text.Json.JsonDocument.Parse(Content(size));
        var parameters = new System.Collections.Generic.Dictionary<string, object?> { ["v"] = json.RootElement };
        string statement = verb is "INSERT" or "UPSERT"
            ? verb + " INTO items (KEY, VALUE) VALUES ('00000',@v),('00001',@v) RETURNING *"
            : verb == "UPDATE" ? "UPDATE items SET text = text RETURNING *" : Statement(verb);
        // Act / Assert
        if (fits)
        {
            await using var result = (await session.ExecuteAsync(statement, parameters, CancellationToken.None))
                .ShouldBeAssignableTo<DatabaseResultSet>();
            result.AffectedCount.ShouldBe(2);
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count++; }
            count.ShouldBe(2);
        }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(statement, parameters, CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            foreach (string id in new[] { "00000", "00001" })
            {
                var document = await target.GetAsync(session, new(id), CancellationToken.None);
                if (verb == "INSERT") { document.ShouldBeNull(); }
                else { document.ShouldNotBeNull().Content.ToArray().ShouldBe(Content(size)); }
            }
            if (original is not null)
            {
                (await target.GetAsync(session, "00000", CancellationToken.None)).ShouldNotBeNull().Version.ShouldBe(original.Value.Version);
            }
        }
    }

    private static byte[] Content(int size) => Encoding.UTF8.GetBytes("{\"text\":\"" + new string('x', size - 11) + "\"}");

    private static string Statement(string verb) => verb == "INSERT"
        ? "INSERT INTO items (KEY k, VALUE v) SELECT META(s).id AS k, s AS v FROM source s RETURNING *"
        : "DELETE FROM items RETURNING *";

    private static async Task PrepareAsync(DocumentDatabaseSession session, string verb, int count, byte[] content)
    {
        var target = await session.CreateCollectionAsync("items", CancellationToken.None);
        var source = verb == "INSERT" ? await session.CreateCollectionAsync("source", CancellationToken.None) : target;
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        for (int index = 0; index < count; index++)
        {
            await source.PutAsync(session, new(index.ToString("D5", CultureInfo.InvariantCulture)), content, cancellationToken: CancellationToken.None);
        }
        await transaction.CommitAsync(CancellationToken.None);
    }
}
