using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// Measures parse strictness (#1101) through a real document session: a stray character and
/// a recognized-unsupported SQL++ word fail at parse time on both session seams, and neither
/// reads, writes nor changes the index catalog.
/// </summary>
public sealed class OqlParseStrictnessExecutionTests
{
    /// <param name="oql">A statement with a stray character or an unsupported SQL++ word.</param>
    /// <param name="code">The single error code it reports.</param>
    [Theory(DisplayName = "Cohesion Test [Documents] - Strictness: stray characters and SQL++ words fail at parse time and execute nothing")]
    [InlineData("SELECT * FROM items ^", "OQL0002")]
    [InlineData("SELECT * FROM items WHERE items.amount = ?", "OQL0002")]
    [InlineData("SELECT ? FROM items", "OQL0002")]
    [InlineData("CREATE INDEX by_name ON items (name) #", "OQL0002")]
    [InlineData("SELECT * FROM items WHERE amount = 2 LIMIT 5", "COHDBL001")]
    [InlineData("SELECT * FROM items WHERE name = 'alpha' LIMIT 1", "COHDBL001")]
    [InlineData("SELECT * FROM items ORDER BY amount DESC OFFSET 2", "COHDBL001")]
    [InlineData("SELECT * FROM items UNNEST items.tags AS tag", "COHDBL001")]
    [InlineData("SELECT * FROM items WHERE EVERY tag IN items.tags SATISFIES tag = 'blue' END", "COHDBL001")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('z', {'name': 'zeta'})", "COHDBL001")]
    [InlineData("MERGE INTO items USING other ON items.name = other.name WHEN MATCHED THEN DELETE", "COHDBL001")]
    public async Task Execute_StrictnessViolation_FailsAtParseTimeWithoutEffectAsync(string oql, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("strictness", CancellationToken.None);
        var collection = await database.CreateCollectionAsync("items", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("""{"name":"alpha","amount":2,"tags":["blue"]}"""),
            cancellationToken: CancellationToken.None);
        var statement = new OqlQueryParser().Parse(oql).ShouldBeOfType<OqlQueryStatement>();

        // Act
        var text = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(oql, cancellationToken: CancellationToken.None));
        var typed = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(new DocumentQueryRequest(statement), CancellationToken.None));

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        text.Message.ShouldStartWith($"OQL parse error {code}: ", Case.Sensitive);
        typed.Message.ShouldBe(text.Message);
        (await RowsAsync(session, "SELECT name, amount FROM items")).ShouldBe(new[] { new object?[] { "alpha", 2m } });
        (await RowsAsync(session, "SELECT INDEX_NAME FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
    }

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string oql)
    {
        await using var result = (await session.ExecuteAsync(oql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
