using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Predicates: SQL++ NULL and MISSING truth tables")]
    [InlineData("NULL IN (1)", "N")]
    [InlineData("MISSING IN (1, NULL)", "M")]
    [InlineData("NULL IN (MISSING)", "N")]
    [InlineData("1 IN (NULL, 1)", "T")]
    [InlineData("1 IN (2, NULL)", "N")]
    [InlineData("1 IN (MISSING, 1, NULL)", "T")]
    [InlineData("1 IN (2, MISSING)", "M")]
    [InlineData("1 IN (MISSING, NULL)", "N")]
    [InlineData("1 IN (NULL, MISSING)", "N")]
    [InlineData("1 IN ('1')", "F")]
    [InlineData("1 NOT IN (2)", "T")]
    [InlineData("1 NOT IN (NULL)", "N")]
    [InlineData("1 NOT IN (MISSING)", "M")]
    [InlineData("NULL BETWEEN 1 AND 3", "N")]
    [InlineData("NULL NOT BETWEEN 1 AND 3", "N")]
    [InlineData("2 BETWEEN 3 AND 1", "F")]
    [InlineData("2 NOT BETWEEN 3 AND 1", "T")]
    [InlineData("2 BETWEEN '1' AND 3", "F")]
    [InlineData("MISSING BETWEEN 1 AND 3", "M")]
    [InlineData("0 BETWEEN 1 AND MISSING", "M")]
    [InlineData("4 BETWEEN MISSING AND 3", "M")]
    [InlineData("0 BETWEEN 1 AND NULL", "N")]
    [InlineData("NULL BETWEEN MISSING AND 3", "M")]
    [InlineData("NULL LIKE '%'", "N")]
    [InlineData("MISSING LIKE '%'", "M")]
    [InlineData("'x' LIKE MISSING", "M")]
    [InlineData("NULL LIKE MISSING", "M")]
    [InlineData("'x' LIKE '%' ESCAPE MISSING", "M")]
    [InlineData("'x' LIKE '%' ESCAPE NULL", "N")]
    [InlineData("1 LIKE '%'", "N")]
    [InlineData("'x' LIKE 1", "N")]
    [InlineData("'Ab' LIKE 'a%'", "F")]
    [InlineData("'a\nb' LIKE 'a_b'", "T")]
    [InlineData("'😀' LIKE '_'", "T")]
    [InlineData("'😀' LIKE '__'", "F")]
    [InlineData("'x%' LIKE 'x\\%'", "T")]
    [InlineData("'x_' LIKE 'x\\_'", "T")]
    [InlineData("'x\\' LIKE 'x\\\\'", "T")]
    [InlineData("'x%' NOT LIKE 'x!%%' ESCAPE '!'", "F")]
    [InlineData("'x\\' LIKE 'x\\' ESCAPE ''", "T")]
    [InlineData("'x%' LIKE 'x😀%' ESCAPE '😀'", "T")]
    [InlineData("CASE MISSING WHEN 1 THEN TRUE END", "N")]
    [InlineData("CASE MISSING WHEN MISSING THEN TRUE ELSE FALSE END", "F")]
    [InlineData("CASE NULL WHEN NULL THEN TRUE END", "N")]
    [InlineData("CASE 1 WHEN '1' THEN FALSE WHEN 1 THEN TRUE END", "T")]
    [InlineData("CASE WHEN MISSING THEN TRUE WHEN NULL THEN TRUE END", "N")]
    [InlineData("CASE WHEN FALSE THEN 1 / 0 ELSE TRUE END", "T")]
    [InlineData("CASE WHEN TRUE THEN MISSING ELSE FALSE END", "M")]
    public async Task Execute_PredicateTruthTable_ShouldPreserveUnknownValuesAsync(string expression, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "one", "{}");
        // Act
        var row = (await RowsAsync(session, $"SELECT {expression} AS result FROM items")).Single();
        var filtered = await RowsAsync(session, $"SELECT 1 FROM items WHERE {expression}");
        // Assert
        row.IsMissing(0).ShouldBe(expected == "M");
        row.IsNull(0).ShouldBe(expected == "N");
        row.GetValue(0).ShouldBe(expected == "T" ? true : expected == "F" ? false : null);
        filtered.Count.ShouldBe(expected == "T" ? 1 : 0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - IN: a parenthesized array parameter remains one structural item")]
    public async Task Execute_ArrayListItem_ShouldCompareStructurallyAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"tags\":[\"blue\"]}");
        using var json = JsonDocument.Parse("[\"blue\"]");
        var parameters = new Dictionary<string, object?> { ["expected"] = json.RootElement };
        // Act / Assert
        (await RowsAsync(session, "SELECT 1 FROM items WHERE tags IN (@expected)", parameters)).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT 1 FROM items WHERE 'blue' IN (tags)")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - LIKE: malformed patterns retain a fixed located diagnostic")]
    [InlineData("'x' LIKE 'z\\'", null, "LIKE pattern must not end with the escape character.")]
    [InlineData("'x' LIKE '%' ESCAPE @escape", "ab", "LIKE ESCAPE must be empty or one Unicode scalar.")]
    public async Task Execute_InvalidLike_ShouldReturnLocatedErrorAsync(string expression, string? escape, string message)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "one", "{}");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session,
            $"SELECT {expression} FROM items", new Dictionary<string, object?> { ["escape"] = escape }));
        // Assert
        error.Message.ShouldBe(message);
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDOC013");
        diagnostic.Start.ShouldBe(escape is null ? 7 : 27);
        diagnostic.End.ShouldBe(7 + expression.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - LIKE: repeated percent patterns have bounded matching time")]
    public async Task Execute_AdversarialLike_ShouldFinishWithinFiveSecondsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "one", "{}");
        var parameters = new Dictionary<string, object?> { ["input"] = new string('a', 10_000) };
        // Act
        var rows = await Task.Run(() => RowsAsync(session, "SELECT @input LIKE '%a%a%a%a%a%a%a%a%b' FROM items", parameters))
            .WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        rows.Single().GetValue(0).ShouldBe(false);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Predicates: inclusive BETWEEN seeks and IN/LIKE scans preserve results")]
    [InlineData("score BETWEEN 2 AND 3", true)]
    [InlineData("score BETWEEN 3 AND 2", true)]
    [InlineData("score IN (2, 3)", false)]
    [InlineData("name LIKE '%ta'", false)]
    public async Task Plan_PredicateWithIndex_ShouldMatchScanAsync(string predicate, bool seek)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"name\":\"alpha\",\"score\":1}");
        await PutAsync(collection, session, "b", "{\"name\":\"beta\",\"score\":2}");
        await PutAsync(collection, session, "c", "{\"name\":\"delta\",\"score\":3}");
        await PutAsync(collection, session, "d", "{\"name\":\"null\",\"score\":null}");
        await PutAsync(collection, session, "e", "{\"name\":\"missing\"}");
        await PutAsync(collection, session, "f", "{\"name\":\"text\",\"score\":\"2\"}");
        string query = $"SELECT name FROM items WHERE {predicate}";
        var before = await RowsAsync(session, query);
        await session.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_name ON items (name)", cancellationToken: CancellationToken.None);
        // Act
        var plan = await PlanAsync(database, query);
        var after = await RowsAsync(session, query);
        // Assert
        after.Select(row => row.GetString(0)).ShouldBe(before.Select(row => row.GetString(0)));
        if (seek)
        {
            var index = plan.Access.ShouldBeOfType<DocumentIndexPath>();
            index.Lower!.Value.Inclusive.ShouldBeTrue();
            index.Upper!.Value.Inclusive.ShouldBeTrue();
            index.Lower.Value.Value.ShouldBe(predicate.Contains("BETWEEN 2", StringComparison.Ordinal) ? 2m : 3m);
            index.Upper.Value.Value.ShouldBe(predicate.Contains("BETWEEN 2", StringComparison.Ordinal) ? 3m : 2m);
        }
        else { plan.Access.ShouldBeOfType<DocumentScanPath>(); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Predicates: nested inputs are validated even over an empty source")]
    [InlineData("1 IN (@p)")]
    [InlineData("1 BETWEEN @p AND 3")]
    [InlineData("'x' LIKE '%' ESCAPE @p")]
    [InlineData("CASE WHEN FALSE THEN @p ELSE 1 END")]
    public async Task Execute_EmptySourceMissingParameter_ShouldValidateNestedInputAsync(string expression)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, $"SELECT {expression} FROM items"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC005");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Predicates: grouped expressions retain structure and source aliases")]
    [InlineData("score IN (2, 3)", "d.score IN (2, 3)")]
    [InlineData("score BETWEEN 2 AND 3", "d.score BETWEEN 2 AND 3")]
    [InlineData("name LIKE 'a%' ESCAPE '!'", "d.name LIKE 'a%' ESCAPE '!'")]
    [InlineData("CASE score WHEN 2 THEN 'two' ELSE 'other' END", "CASE d.score WHEN 2 THEN 'two' ELSE 'other' END")]
    public async Task Execute_GroupedPredicate_ShouldBindEquivalentStructureAsync(string key, string projected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"name\":\"alpha\",\"score\":2}");
        await PutAsync(collection, session, "b", "{\"name\":\"beta\",\"score\":4}");
        // Act
        var rows = await RowsAsync(session, $"SELECT {projected} AS k, COUNT(*) AS n FROM items d GROUP BY {key} ORDER BY k");
        // Assert
        rows.Count.ShouldBe(2);
        rows.Select(row => row.GetInt32(1)).ShouldBe([1, 1]);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Predicates: nested metadata and aggregate placement cannot bypass validation")]
    [InlineData("1 IN (COUNT(*))")]
    [InlineData("1 BETWEEN 0 AND COUNT(*)")]
    [InlineData("'x' LIKE '%' ESCAPE COUNT(*)")]
    [InlineData("CASE WHEN FALSE THEN COUNT(*) ELSE TRUE END")]
    [InlineData("1 IN (META(other).id)")]
    [InlineData("1 BETWEEN 0 AND META(other).id")]
    [InlineData("'x' LIKE META(other).id")]
    [InlineData("CASE WHEN FALSE THEN META(other).id ELSE TRUE END")]
    public async Task Execute_InvalidNestedContext_ShouldRejectBeforeReadingAsync(string predicate)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, $"SELECT 1 FROM items d WHERE {predicate}"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC001");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - IN: list expressions evaluate before membership unknown propagation")]
    [InlineData("1 IN (1, 1 / 0)")]
    [InlineData("NULL IN (1 / 0)")]
    [InlineData("MISSING IN (1 / 0)")]
    public async Task Execute_ErrorInList_ShouldEvaluateAllItemsAsync(string expression)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("predicates", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "one", "{}");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session, $"SELECT {expression} FROM items"));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC006");
    }
}
