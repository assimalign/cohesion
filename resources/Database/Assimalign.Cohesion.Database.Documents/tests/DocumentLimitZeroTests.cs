using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentLimitZeroTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - LIMIT zero: blocking queries skip runtime failures for every source kind")]
    [InlineData("SELECT RAW v FROM [1/0] AS v ORDER BY v LIMIT 0")]
    [InlineData("SELECT DISTINCT RAW v FROM [1/0] AS v LIMIT 0")]
    [InlineData("SELECT COUNT(*) FROM [1/0] AS v LIMIT 0")]
    [InlineData("SELECT RAW d FROM (SELECT RAW v FROM [1/0] AS v) d ORDER BY d LIMIT 0")]
    [InlineData("SELECT v FROM [1/0] AS v GROUP BY v LIMIT 0")]
    [InlineData("SELECT RAW n FROM items WHERE 1/0 = 1 ORDER BY n LIMIT 0")]
    [InlineData("SELECT RAW OBJECT_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE 1/0 = 1 ORDER BY OBJECT_NAME LIMIT 0")]
    [InlineData("SELECT COUNT(*) FROM MISSING AS v HAVING 1/0 = 1 LIMIT 0")]
    public async Task ExecuteAsync_ZeroLimitBlockingSource_ShouldSkipRuntimeEvaluationAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("zero-limit-errors", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);

        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);

        // Assert
        rows.ShouldBeEmpty();
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - LIMIT zero: no callbacks or reads and planned result metadata survives")]
    [InlineData("SELECT RAW v FROM [tick(1)] AS v ORDER BY v LIMIT @zero", "value", DatabaseType.Null)]
    [InlineData("SELECT DISTINCT tick(v) AS n FROM [1] AS v LIMIT @zero", "n", DatabaseType.Decimal)]
    [InlineData("SELECT COUNT(tick(v)) AS n FROM [1] AS v LIMIT @zero", "n", DatabaseType.Int64)]
    [InlineData("SELECT tick(v) AS n FROM [1] AS v GROUP BY v LIMIT @zero", "n", DatabaseType.Decimal)]
    [InlineData("SELECT RAW d FROM (SELECT RAW tick(n) FROM items) d ORDER BY d LIMIT @zero", "value", DatabaseType.Null)]
    [InlineData("SELECT RAW tick(n) FROM items LIMIT @zero", "value", DatabaseType.Decimal)]
    [InlineData("SELECT n FROM items WHERE tick(n) > 0 LIMIT @zero", "n", DatabaseType.Null)]
    [InlineData("SELECT * FROM items WHERE tick(n) > 0 LIMIT @zero", "document", DatabaseType.Json)]
    [InlineData("SELECT tick(n) AS n FROM items ORDER BY n LIMIT @zero", "n", DatabaseType.Decimal)]
    [InlineData("SELECT COUNT(tick(n)) AS n FROM items LIMIT @zero", "n", DatabaseType.Int64)]
    [InlineData("SELECT tick(1) AS n FROM COHESION_SCHEMA.OBJECT_OWNERSHIP ORDER BY n LIMIT @zero", "n", DatabaseType.Decimal)]
    [InlineData("SELECT (SELECT RAW tick(n) FROM items) AS n FROM items ORDER BY n LIMIT @zero", "n", DatabaseType.Json)]
    [InlineData("SELECT RAW d FROM (SELECT RAW tick(n) FROM items ORDER BY n LIMIT @zero) d", "value", DatabaseType.Null)]
    [InlineData("SELECT RAW d FROM (SELECT RAW tick(n) FROM items LIMIT @zero) d", "value", DatabaseType.Null)]
    public async Task ExecuteAsync_ZeroLimitSource_ShouldSkipEffectsAndKeepMetadataAsync(string query, string name, DatabaseType type)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("zero-limit-effects");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        var parameters = new Dictionary<string, object?> { ["zero"] = 0L };

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, parameters, CancellationToken.None);
        var rows = new List<DatabaseRow>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }

        // Assert
        rows.ShouldBeEmpty();
        calls.ShouldBe(0);
        session.LastQueryDocumentsRead.ShouldBe(0);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
        result.AffectedCount.ShouldBe(-1);
        result.Diagnostics.ShouldBeEmpty();
        var column = result.Columns.ShouldHaveSingleItem();
        column.Name.ShouldBe(name);
        column.Ordinal.ShouldBe(0);
        column.Type.ShouldBe(type);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - LIMIT zero: static source validation remains eager")]
    [InlineData("SELECT RAW v FROM @absent AS v ORDER BY v LIMIT 0", "CD01005")]
    [InlineData("SELECT RAW d FROM (SELECT n FROM absent) d ORDER BY d LIMIT 0", "CD01002")]
    [InlineData("SELECT RAW d FROM (SELECT n FROM items WHERE n = @absent) d ORDER BY d LIMIT 0", "CD01005")]
    [InlineData("SELECT RAW v FROM [unbound] AS v ORDER BY v LIMIT 0", "CD01034")]
    public async Task ExecuteAsync_ZeroLimitInvalidSource_ShouldKeepStaticDiagnosticAsync(string query, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("zero-limit-validation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        session.LastQueryDocumentsRead.ShouldBe(0);
    }
}
