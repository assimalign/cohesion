using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The planner compiles each expression once into an engine-owned bound tree that the evaluator
/// walks per row (phase E1 of the engine extensibility program): columns as ordinals, calls bound to
/// their functions, comparisons with their collations, and every failure the unbound evaluator
/// raised only when it reached a node deferred to that node.
/// </summary>
public sealed class SqlBoundExpressionTests
{
    /// <summary>
    /// A predicate binds once to ordinals, functions and fixed collations: nothing in the bound tree
    /// names a column or a function, so evaluating it resolves nothing per row.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Bound expressions: binding resolves ordinals, functions and collations once")]
    public void Bind_PredicateOverColumnsAndCalls_ShouldResolveOrdinalsFunctionsAndCollations()
    {
        // Arrange
        var columns = new[]
        {
            new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32)),
            new SqlCatalogColumn("name", new DatabaseTypeInfo(DatabaseType.String), collation: Collation.CaseInsensitive),
        };
        var scope = new SqlExpressionEvaluator(columns, null);

        // Act
        var bound = scope.Bind(Predicate("UPPER(name) = 'ANN' AND ABS(id) > 1"));

        // Assert
        var chain = bound.ShouldBeOfType<SqlBoundLogical>();
        chain.IsOr.ShouldBeFalse();
        var equality = chain.Operands[0].ShouldBeOfType<SqlBoundBinary>();
        var upper = equality.Left.ShouldBeOfType<SqlBoundCall>();
        upper.Function.ShouldBe(SqlBuiltinFunction.Upper);
        upper.Arguments.ShouldHaveSingleItem().ShouldBeOfType<SqlBoundColumn>().Ordinal.ShouldBe(1);
        equality.Right.ShouldBeOfType<SqlBoundConstant>().Value.ShouldBe("ANN");
        equality.Collation.IsFixed.ShouldBeTrue();
        equality.Collation.Resolve(null).ShouldBe(Collation.CaseInsensitive);
        var magnitude = chain.Operands[1].ShouldBeOfType<SqlBoundBinary>();
        magnitude.Left.ShouldBeOfType<SqlBoundCall>().Arguments[0].ShouldBeOfType<SqlBoundColumn>().Ordinal.ShouldBe(0);
        magnitude.Right.ShouldBeOfType<SqlBoundConstant>().Value.ShouldBe(1L);
        scope.Evaluate(bound, [2, "ann"]).ShouldBe(true);
        scope.Evaluate(bound, [1, "ann"]).ShouldBe(false);
    }

    /// <summary>
    /// A term the evaluator cannot compute binds without failing and fails only when evaluated: a
    /// statement whose short circuit never reaches it succeeds over a populated table, as it did
    /// when the evaluator met such a term per row, and fails the same way once a row reaches it.
    /// </summary>
    /// <param name="term">A term that fails when evaluated.</param>
    /// <param name="failure">The start of the error it raises.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Bound expressions: a failing term fails only when a row reaches it")]
    [InlineData("NULLIF(id, 1) = 1", "Function 'NULLIF' is not supported by the executor yet.")]
    [InlineData("id = @missing", "No value was supplied for parameter 'missing'.")]
    [InlineData("id > 99999999999999999999", "COHSQLE002: Numeric value out of range: integer literal 99999999999999999999 exceeds BIGINT.")]
    public async Task ExecuteAsync_FailingTermBehindShortCircuit_ShouldFailOnlyWhenReached(string term, string failure)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-bound-deferred" });
        var database = await engine.CreateDatabaseAsync("bound");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2)");
        var parameters = new Dictionary<string, object?>();

        // Act
        var skipped = await RowsAsync(session, $"SELECT id FROM t WHERE id < 0 AND {term}", parameters);
        await ExecuteAsync(session, "INSERT INTO t VALUES (-1)");
        var reached = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, $"SELECT id FROM t WHERE id < 0 AND {term}", parameters));

        // Assert
        skipped.ShouldBeEmpty();
        reached.Message.ShouldStartWith(failure, Case.Sensitive);
    }

    /// <summary>
    /// A comparison over an <c>IN (subquery)</c> term takes the subquery column's collation only
    /// when the subquery returned rows, and the IN operand's when it returned none, as it did when
    /// the evaluator searched the materialized values for it per row. Here the concatenation
    /// <c>'False' || 'x'</c> matches <c>'falsex'</c> only under the subquery's case-insensitive
    /// collation.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Bound expressions: an IN subquery's collation applies only when it returned rows")]
    public async Task ExecuteAsync_InSubqueryUnderConcatenation_ShouldTakeTheSubqueryCollationOnlyWhenItReturnedRows()
    {
        // Arrange
        const string query = "SELECT id FROM t WHERE ('a' IN (SELECT s FROM u)) || 'x' = 'falsex'";
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-bound-in-collation" });
        var database = await engine.CreateDatabaseAsync("bound");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "CREATE TABLE u (s TEXT COLLATE case_insensitive)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");

        // Act
        var empty = await RowsAsync(session, query);
        await ExecuteAsync(session, "INSERT INTO u VALUES ('zzz')");
        var populated = await RowsAsync(session, query);

        // Assert
        empty.ShouldBeEmpty();
        populated.ShouldHaveSingleItem().ShouldBe([1]);
    }

    /// <summary>
    /// A persisted CHECK binds to one tree per table version, which every session's write evaluates;
    /// writes neither parse nor bind it again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Bound expressions: a CHECK binds once per table version and every session evaluates that tree")]
    public async Task ExecuteAsync_Check_ShouldEvaluateTheTreeBoundOncePerTableVersion()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-bound-check" });
        var database = await engine.CreateDatabaseAsync("bound");
        await using var first = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await using var second = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(first, "CREATE TABLE t (id INT, name TEXT, CONSTRAINT ck_t CHECK (LENGTH(name) > 0 AND ABS(id) < 100))");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var check = database.Definitions.Get(table).Checks.ShouldHaveSingleItem();
        long binds = database.Definitions.BindCount;

        // Act
        await ExecuteAsync(first, "INSERT INTO t VALUES (1, 'a')");
        await ExecuteAsync(second, "INSERT INTO t VALUES (-2, 'b')");
        var violation = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(second, "INSERT INTO t VALUES (100, 'c')"));

        // Assert
        violation.ShouldBeOfType<SqlConstraintViolationException>();
        database.Definitions.BindCount.ShouldBe(binds);
        database.Definitions.Get(table).Checks.ShouldHaveSingleItem().Bound.ShouldBeSameAs(check.Bound);
        check.Bound.ShouldBeOfType<SqlBoundLogical>().Operands.ShouldAllBe(operand => operand.Kind == SqlBoundExpressionKind.Binary);
    }

    private static SqlExpression Predicate(string predicate)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT * FROM t WHERE {predicate}");
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return ((SqlSelectExpression)statement.SqlExpression!).Where!;
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string statement)
    {
        var result = await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(QueryResultStatus.Success, statement);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string statement,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(statement, parameters, CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }
}
