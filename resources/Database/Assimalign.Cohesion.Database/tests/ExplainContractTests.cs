using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class ExplainContractTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - EXPLAIN: all languages return the identical shared column contract")]
    public async Task Execute_ThreeLanguages_ShouldReturnIdenticalColumnsAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        await using var sql = SqlDatabaseEngine.Create("sql-explain", new());
        await using var documents = DocumentDatabaseEngine.Create("documents-explain", new());
        await using var graph = GraphDatabaseEngine.Create("graph-explain", new());
        await using var sqlSession = await (await sql.CreateDatabaseAsync("explain", token)).CreateSessionAsync(cancellationToken: token);
        await using var documentSession = await (await documents.CreateDatabaseAsync("explain", token)).CreateSessionAsync(token);
        await using var graphSession = await (await graph.CreateDatabaseAsync("explain", token)).CreateSessionAsync(token);
        await sqlSession.ExecuteAsync("CREATE TABLE items (id INT);", cancellationToken: token);
        await documentSession.CreateCollectionAsync("items", token);

        // Act
        DatabaseResult[] results =
        [
            await sqlSession.ExecuteAsync("EXPLAIN SELECT id FROM items;", cancellationToken: token),
            await documentSession.ExecuteAsync("EXPLAIN SELECT id FROM items", cancellationToken: token),
            await graphSession.ExecuteAsync("EXPLAIN MATCH (n) RETURN n", cancellationToken: token),
        ];

        // Assert
        string[] names = ["node_id", "parent_id", "operator", "target", "index", "detail", "estimated_rows", "actual_rows"];
        DatabaseType[] types = [DatabaseType.Int32, DatabaseType.Int32, DatabaseType.String, DatabaseType.String,
            DatabaseType.String, DatabaseType.String, DatabaseType.Int64, DatabaseType.Int64];
        foreach (var result in results)
        {
            await using var description = result.ShouldBeOfType<DatabasePlanDescription>();
            description.Status.ShouldBe(DatabaseResultStatus.Success);
            description.AffectedCount.ShouldBe(-1);
            description.Columns.Select(column => column.Name).ShouldBe(names);
            description.Columns.Select(column => column.Type).ShouldBe(types);
            description.Columns.Select(column => column.Ordinal).ShouldBe(Enumerable.Range(0, 8));
            description.Columns.Select(column => column.IsNullable).ShouldBe([false, true, false, true, true, true, true, true]);
            int next = 0;
            var ancestors = new List<int>();
            await foreach (var row in description.GetRowsAsync(token))
            {
                row.GetInt32(0).ShouldBe(next++);
                row.IsNull(6).ShouldBeTrue();
                row.IsNull(7).ShouldBeTrue();
                if (row.IsNull(1)) { ancestors.Clear(); }
                else
                {
                    int parent = row.GetInt32(1);
                    ancestors.ShouldContain(parent, "A parent must precede its descendants in depth-first pre-order.");
                    ancestors.RemoveRange(ancestors.IndexOf(parent) + 1, ancestors.Count - ancestors.IndexOf(parent) - 1);
                }
                ancestors.Add(row.GetInt32(0));
            }
            next.ShouldBeGreaterThan(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database] - EXPLAIN: unsupported prefixes fail at parse time with COHDBL001")]
    [InlineData("SQL", "EXPLAIN CREATE TABLE t (id INT)")]
    [InlineData("SQL", "EXPLAIN BEGIN")]
    [InlineData("SQL", "EXPLAIN EXPLAIN SELECT * FROM t")]
    [InlineData("OQL", "EXPLAIN CREATE INDEX ix ON c (a)")]
    [InlineData("OQL", "EXPLAIN CREATE COLLECTION c")]
    [InlineData("OQL", "EXPLAIN BEGIN")]
    [InlineData("OQL", "EXPLAIN EXPLAIN SELECT * FROM c")]
    [InlineData("OQL", "EXPLAIN ANALYZE SELECT * FROM c")]
    [InlineData("GQL", "EXPLAIN SHOW LABELS")]
    [InlineData("GQL", "EXPLAIN BEGIN")]
    [InlineData("GQL", "EXPLAIN EXPLAIN MATCH (n) RETURN n")]
    [InlineData("GQL", "EXPLAIN ANALYZE MATCH (n) RETURN n")]
    [InlineData("GQL", "PROFILE MATCH (n) RETURN n")]
    public void Parse_UnsupportedExplain_ShouldReportCapability(string language, string source)
    {
        // Act
        var statement = language switch
        {
            "SQL" => new SqlQueryParser().Parse(source),
            "OQL" => new OqlQueryParser().Parse(source),
            _ => new GqlQueryParser().Parse(source),
        };
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHDBL001" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - EXPLAIN ANALYZE: SQL retains the shared columns and reports executed candidates (#1522)")]
    public async Task Execute_AnalyzedSql_ShouldRetainTheSharedContractAsync()
    {
        // Forty seconds is a hang guard, not a performance assertion.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        await using var engine = SqlDatabaseEngine.Create("sql-analyzed-contract", new());
        await using var session = await (await engine.CreateDatabaseAsync("explain", token)).CreateSessionAsync(cancellationToken: token);
        await session.ExecuteAsync("CREATE TABLE items (id INT);", cancellationToken: token);
        await session.ExecuteAsync("INSERT INTO items VALUES (1),(2);", cancellationToken: token);

        await using var plain = (DatabasePlanDescription)await session.ExecuteAsync("EXPLAIN SELECT id FROM items;", cancellationToken: token);
        await using var analyzed = (DatabasePlanDescription)await session.ExecuteAsync("EXPLAIN ANALYZE SELECT id FROM items;", cancellationToken: token);

        analyzed.Columns.Select(column => (column.Name, column.Type, column.Ordinal, column.IsNullable))
            .ShouldBe(plain.Columns.Select(column => (column.Name, column.Type, column.Ordinal, column.IsNullable)));
        await foreach (var row in plain.GetRowsAsync(token)) { row.IsNull(7).ShouldBeTrue(); }
        int measured = 0;
        await foreach (var row in analyzed.GetRowsAsync(token))
        {
            row.FieldCount.ShouldBe(8);
            if (!row.IsNull(7)) { row.GetInt64(7).ShouldBe(2); measured++; }
        }
        measured.ShouldBe(2, "Both Select and its TableScan report the two candidates.");
    }

    [Theory(DisplayName = "Cohesion Test [Database] - EXPLAIN: every language rejects trailing text after the described statement")]
    [InlineData("EXPLAIN SELECT * FROM items", "EXPLAIN SELECT * FROM items", "EXPLAIN MATCH (n) RETURN n")]
    public void Parse_ExplainWithLeftovers_ShouldReject(string sql, string oql, string gql)
    {
        // Act / Assert
        foreach (string suffix in new[] { " x y", "; garbage", "; DELETE FROM items" })
        {
            new SqlQueryParser().Parse(sql + suffix).Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
            new OqlQueryParser().Parse(oql + suffix).Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
            new GqlQueryParser().Parse(gql + suffix).Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
        }
    }
}
