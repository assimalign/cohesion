using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSubqueryExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Grouping: repeated subquery keys bind in projections, HAVING and ORDER BY")]
    [InlineData("c.id = (SELECT 2)")]
    [InlineData("c.id = ANY (SELECT 2)")]
    [InlineData("c.id = SOME (SELECT 2)")]
    [InlineData("c.id = ALL (SELECT 2)")]
    [InlineData("EXISTS (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("c.id = (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("c.id = ANY (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("c.id = SOME (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("c.id = ALL (SELECT d.id FROM choices d WHERE d.id IS NOT NULL AND (d.id = c.id OR d.id = 2))")]
    public async Task ExecuteAsync_RepeatedSubqueryGroupKey_ShouldReuseGroupValue(string key)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery-grouping");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        var projected = await RowsAsync(session,
            $"SELECT {key}, COUNT(*) FROM candidates c GROUP BY {key} ORDER BY {key} NULLS LAST;");
        var filtered = await RowsAsync(session,
            $"SELECT COUNT(*) FROM candidates c GROUP BY {key} HAVING {key};");
        var ordered = await RowsAsync(session,
            $"SELECT COUNT(*) FROM candidates c GROUP BY {key} ORDER BY {key} NULLS LAST;");

        // Assert: exactly one candidate satisfies each predicate; every candidate belongs to a group.
        projected.Sum(row => (long)row[1]!).ShouldBe(4);
        projected.Single(row => Equals(row[0], true))[1].ShouldBe(1L);
        projected.Select(row => row[1]).ShouldBe(ordered.Select(row => row[0]));
        filtered.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Grouping: distinct subquery bodies, correlations, quantifiers and operators do not share a slot")]
    [InlineData("c.id = (SELECT 2)", "c.id = (SELECT 3)")]
    [InlineData("c.id = ANY (SELECT d.id FROM choices d WHERE d.id = 2)", "c.id = ANY (SELECT d.id FROM choices d WHERE d.id = 3)")]
    [InlineData("EXISTS (SELECT d.id FROM choices d WHERE d.id = c.id)", "EXISTS (SELECT d.id FROM choices d WHERE d.id <> c.id)")]
    [InlineData("c.id = (SELECT d.id FROM choices d WHERE d.id = c.id)", "c.id = (SELECT d.id FROM choices d WHERE d.id = c.other_id)")]
    [InlineData("c.id = ANY (SELECT d.id FROM choices d WHERE d.id = c.id)", "c.id = ANY (SELECT d.id FROM choices d WHERE d.id = c.other_id)")]
    [InlineData("c.id = ANY (SELECT 2)", "c.id = ALL (SELECT 2)")]
    [InlineData("c.id = ANY (SELECT 2)", "c.id = SOME (SELECT 2)")]
    [InlineData("c.id = ANY (SELECT 2)", "c.id <> ANY (SELECT 2)")]
    [InlineData("c.id = ANY (SELECT d.id FROM choices d WHERE d.id = c.id)", "c.other_id = ANY (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("c.id = (SELECT d.id FROM choices d WHERE d.id = c.id)", "c.id <> (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("EXISTS (SELECT d.id FROM choices d WHERE d.id = c.id)", "NOT EXISTS (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("c.id IN (SELECT 2)", "c.id IN (SELECT 3)")]
    public async Task ExecuteAsync_DifferentSubqueryGroupKey_ShouldRejectUngroupedColumn(string key, string other)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery-grouping");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);
        await ExecuteAsync(session, "ALTER TABLE candidates ADD COLUMN other_id INT;");
        string[] queries =
        [
            $"SELECT {other}, COUNT(*) FROM candidates c GROUP BY {key};",
            $"SELECT COUNT(*) FROM candidates c GROUP BY {key} HAVING {other};",
            $"SELECT COUNT(*) FROM candidates c GROUP BY {key} ORDER BY {other};",
        ];

        foreach (string query in queries)
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, query));

            // Assert
            error.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldContain("CS05017", query);
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Grouping: subquery keys retain bound-column identity for scalar operands")]
    [InlineData("id = (SELECT 2)", "c.id = (SELECT 2)")]
    [InlineData("id = ANY (SELECT d.id FROM choices d WHERE d.id = c.id)", "c.id = ANY (SELECT d.id FROM choices d WHERE d.id = c.id)")]
    [InlineData("(id + 1) = ANY (SELECT d.id + 1 FROM choices d WHERE d.id = c.id)", "(c.id + 1) = ANY (SELECT d.id + 1 FROM choices d WHERE d.id = c.id)")]
    public async Task ExecuteAsync_EquivalentSubqueryGroupOperands_ShouldUseBoundColumns(string key, string repeated)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("subquery-grouping");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        var rows = await RowsAsync(session,
            $"SELECT {repeated}, COUNT(*) FROM candidates c GROUP BY {key} HAVING {repeated} ORDER BY {repeated};");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { true, 1L });
    }
}
