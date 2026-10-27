using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSubqueryExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: persisted equivalence includes the operator, spelling, operand and query")]
    [InlineData("1 = ANY (SELECT 2)", true)]
    [InlineData("1 <> ANY (SELECT 2)", false)]
    [InlineData("1 = SOME (SELECT 2)", false)]
    [InlineData("3 = ANY (SELECT 2)", false)]
    [InlineData("1 = ANY (SELECT 3)", false)]
    public void AreEquivalent_QuantifiedTree_ShouldCompareAllSyntax(string other, bool expected)
    {
        // Arrange
        static SqlExpression Parse(string expression) => ((SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {expression};"))
            .SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
        var first = Parse("1 = ANY (SELECT 2)");
        var second = Parse(other);

        // Act / Assert
        SqlPersistedExpression.AreEquivalent(first, second).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: all operators preserve the complete three-valued truth table")]
    [InlineData("ANY")]
    [InlineData("SOME")]
    [InlineData("ALL")]
    public async Task ExecuteAsync_QuantifiedTruthTable_ShouldFoldComparisons(string quantifier)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        foreach (var (op, yes, no) in new[] { ("=", 2, 1), ("<>", 1, 2), ("!=", 1, 2), ("<", 3, 1), ("<=", 2, 1), (">", 1, 3), (">=", 2, 3) })
        {
            // Independent truth tables: empty, true, false, unknown, then deciding/unknown mixtures.
            bool?[][] sets = [[], [true], [false], [null], [true, null], [false, null], [null, true, false], [false, true, null]];
            bool?[] any = [false, true, false, null, true, null, true, true];
            bool?[] all = [true, true, false, null, null, false, false, false];
            for (int index = 0; index < sets.Length; index++)
            {
                string source = sets[index].Length == 0 ? "SELECT CAST(NULL AS INT) WHERE FALSE" :
                    string.Join(" UNION ALL ", sets[index].Select(value => "SELECT " +
                        (value is null ? "CAST(NULL AS INT)" : value.Value ? yes.ToString(CultureInfo.InvariantCulture) : no.ToString(CultureInfo.InvariantCulture))));
                // Act
                var rows = await RowsAsync(session, $"SELECT 2 {op} {quantifier} ({source}), CAST(NULL AS INT) {op} {quantifier} ({source});");

                // Assert
                rows.ShouldHaveSingleItem().ShouldBe(new object?[]
                    { quantifier == "ALL" ? all[index] : any[index], index == 0 ? quantifier == "ALL" : null });
            }
        }

    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: binding and shape errors preserve statement atomicity")]
    [InlineData("id = ANY (SELECT id, id FROM choices)", "CS05013")]
    [InlineData("id = ALL (SELECT missing FROM choices)", "CS01015")]
    [InlineData("id = SOME (SELECT 'text' FROM choices)", "CS02055")]
    public async Task ExecuteAsync_QuantifiedFailure_ShouldPreserveRows(string predicate, string code)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, $"DELETE FROM candidates WHERE {predicate};"));

        // Assert
        error.Diagnostics.Select(d => d.Code).ShouldContain(code, error.Message);
        (await RowsAsync(session, "SELECT id FROM candidates;")).Count.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: correlation, nesting, grouping, views and DML share typed slots")]
    public async Task ExecuteAsync_QuantifiedComposition_ShouldBindAllQueryScopes()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, "SELECT c.id, c.id >= ALL (SELECT d.id FROM choices d WHERE d.id <= c.id), c.id = SOME (SELECT d.id FROM choices d WHERE d.id = c.id) FROM candidates c WHERE c.id IS NOT NULL ORDER BY c.id;"))
            .ShouldBe(new object?[][] { [1, true, false], [2, true, true], [3, true, false] });
        (await RowsAsync(session, "WITH q AS (SELECT id FROM choices) SELECT c.id FROM candidates c WHERE c.id = ANY (SELECT q.id FROM q WHERE EXISTS (SELECT id FROM candidates x WHERE x.id = c.id)) ORDER BY c.id;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 2 });
        (await RowsAsync(session, "SELECT c.id, COUNT(*) FROM candidates c GROUP BY c.id HAVING c.id = ANY (SELECT id FROM choices) ORDER BY c.id;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 1L });
        (await RowsAsync(session, "SELECT c.id, (SELECT c.id) = ANY (SELECT d.id FROM choices d WHERE d.id = c.id) FROM candidates c WHERE c.id IS NOT NULL ORDER BY c.id;"))
            .ShouldBe(new object?[][] { [1, false], [2, true], [3, false] });
        await ExecuteAsync(session, "CREATE VIEW picked AS SELECT id FROM candidates WHERE id = ANY (SELECT id FROM choices);");
        (await RowsAsync(session, "SELECT id FROM picked;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 2 });
        (await ExecuteAsync(session, "UPDATE candidates SET id = 9 WHERE id = SOME (SELECT id FROM choices);")).AffectedCount.ShouldBe(1);
        (await ExecuteAsync(session, "DELETE FROM candidates c WHERE c.id > ALL (SELECT id FROM choices d WHERE d.id IS NOT NULL);")).AffectedCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: a volatile scalar operand runs once and empty sets skip it")]
    public async Task ExecuteAsync_QuantifiedVolatileOperand_ShouldEvaluateOncePerNonemptySet()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("quantified-volatility");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) => { calls++; return value; }));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "SELECT observe(2) = ANY (SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3), observe(2) = ALL (SELECT 2 UNION ALL SELECT 2), observe(2) = SOME (SELECT 1 WHERE FALSE);");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { true, true, false });
        calls.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: collation and ordinary numeric and CHAR comparisons cross the subquery boundary")]
    public async Task ExecuteAsync_QuantifiedTypes_ShouldUseComparisonRules()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "SELECT 'A' = ANY (SELECT 'a' COLLATE case_insensitive), 'A' COLLATE binary = ALL (SELECT 'a' COLLATE case_insensitive), 2 < SOME (SELECT 2.5), 'a' = ANY (SELECT CAST('a  ' AS CHAR(3))); ");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { true, false, true, true });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: temporal and binary values retain ordinary comparison semantics")]
    public async Task ExecuteAsync_QuantifiedTemporalAndBinaryValues_ShouldUseScalarOrdering()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session,
            "SELECT DATE '2024-05-08' < ANY (SELECT TIMESTAMP '2024-05-08 12:00:00'), " +
            "TIMESTAMPTZ '2024-05-08 12:00:00+00:00' = ALL (SELECT TIMESTAMPTZ '2024-05-08 14:00:00+02:00'), " +
            "X'007F' < SOME (SELECT X'0080'), X'00FF' = ALL (SELECT X'00FF' UNION ALL SELECT X'00FF');");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { true, true, true, true });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: empty sets skip correlated scalar operands during preparation")]
    [InlineData("ANY", false)]
    [InlineData("SOME", false)]
    [InlineData("ALL", true)]
    public async Task ExecuteAsync_QuantifiedEmptySet_ShouldSkipCorrelatedOperand(string quantifier, bool expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        foreach (string source in new[] { "SELECT id FROM choices WHERE FALSE", "SELECT d.id FROM choices d WHERE d.id = c.id AND FALSE" })
        {
            (await RowsAsync(session, $"SELECT (SELECT 1 / (c.id - c.id)) = {quantifier} ({source}) FROM candidates c WHERE c.id = 2;"))
                .ShouldHaveSingleItem().ShouldBe(new object?[] { expected });
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Quantified comparisons: lazy scalar branches skip correlated errors")]
    [InlineData("CASE WHEN TRUE THEN TRUE ELSE c.id = ANY (SELECT 1 / (d.id - c.id) FROM choices d WHERE d.id = c.id) END")]
    [InlineData("TRUE OR c.id = ALL (SELECT 1 / (d.id - c.id) FROM choices d WHERE d.id = c.id)")]
    [InlineData("COALESCE(TRUE, c.id = SOME (SELECT 1 / (d.id - c.id) FROM choices d WHERE d.id = c.id))")]
    public async Task ExecuteAsync_QuantifiedLazyBranch_ShouldSkipChild(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("quantified");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, $"SELECT {expression} FROM candidates c WHERE c.id = 2;")).ShouldHaveSingleItem().ShouldBe(new object?[] { true });
    }
}
