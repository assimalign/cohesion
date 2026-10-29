using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlAggregateExecutionTests
{
    private const string realStatisticsChoice = "CASE WHEN id=1 THEN 16777217 ELSE CAST(16777216 AS REAL) END";

    [Theory(DisplayName = "Cohesion Test [Sql] - Statistics: nested alternatives convert at every numeric boundary (#1720)")]
    [InlineData("COALESCE(" + realStatisticsChoice + ", CAST(0 AS DOUBLE PRECISION))")]
    [InlineData("CASE WHEN id=1 THEN COALESCE(16777217, CAST(NULL AS REAL)) ELSE CAST(16777216 AS DOUBLE PRECISION) END")]
    [InlineData("COALESCE(CASE WHEN id=1 THEN COALESCE(CASE id WHEN 1 THEN 16777217 END, CAST(0 AS REAL)) ELSE CAST(16777216 AS REAL) END, CAST(0 AS DOUBLE PRECISION))")]
    [InlineData("COALESCE(NULLIF(" + realStatisticsChoice + ", 0), CAST(0 AS DOUBLE PRECISION))")]
    [InlineData("NULLIF(" + realStatisticsChoice + ", 0)")]
    [InlineData("COALESCE(CASE WHEN id=1 THEN CAST(16777217 AS NUMERIC) ELSE CAST(16777216 AS REAL) END, CAST(0 AS DOUBLE PRECISION))")]
    [InlineData("COALESCE(CAST(" + realStatisticsChoice + " AS DOUBLE PRECISION), CAST(0 AS DOUBLE PRECISION))")]
    [InlineData("COALESCE((" + realStatisticsChoice + ") + 0, CAST(0 AS DOUBLE PRECISION))")]
    public async Task Statistics_NestedAlternatives_ShouldConvertBeforeEnclosingExpressions(string expression)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("statistics");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert: the reported nesting keeps ordinary scalar alternative behavior.
        var ordinary = await RowsAsync(session, $"SELECT COALESCE({realStatisticsChoice}, CAST(0 AS DOUBLE PRECISION)) " +
            "FROM (SELECT 1 AS id UNION ALL SELECT 2) s ORDER BY id;");
        ordinary.Select(row => Convert.ToDouble(row[0])).ShouldBe(new[] { 16777217d, 16777216d });

        foreach (string filter in new[] { "", " FILTER (WHERE id > 0)" })
        {
            // Act
            await using var result = (await ExecuteAsync(session,
                $"SELECT VAR_POP({expression}){filter}, VAR_SAMP(DISTINCT {expression}){filter}, STDDEV_POP({expression}){filter} " +
                "FROM (SELECT 1 AS id UNION ALL SELECT 2) s;")).ShouldBeAssignableTo<DatabaseResultSet>();

            // Assert
            result.Columns.ShouldAllBe(column => column.Type == DatabaseType.Float64 && column.IsNullable);
            (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 0d, null, 0d });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FILTER: every native aggregate admits only TRUE rows")]
    [InlineData("")]
    [InlineData(" GROUP BY region")]
    public async Task Filter_AllAggregateForms_ShouldFilterBeforeArguments(string grouping)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("filter");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, region INT, v INT, flag BOOLEAN, label TEXT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (0,1,0,FALSE,'z'),(1,1,2,TRUE,'b'),(2,1,4,TRUE,'a'),(3,1,4,NULL,'c');");

        // Act
        var rows = await RowsAsync(session,
            "SELECT COUNT(*) FILTER (WHERE flag), COUNT(DISTINCT v) FILTER (WHERE flag), " +
            "SUM(ALL 8/v) FILTER (WHERE flag), AVG(v) FILTER (WHERE flag), " +
            "MIN(v) FILTER (WHERE flag), MAX(v) FILTER (WHERE flag), " +
            "STRING_AGG(label, ',' ORDER BY 8/v, label) FILTER (WHERE flag), " +
            "LISTAGG(DISTINCT label, ',') WITHIN GROUP (ORDER BY label) FILTER (WHERE flag), " +
            "BOOL_AND(flag) FILTER (WHERE flag), BOOL_OR(flag) FILTER (WHERE flag), EVERY(flag) FILTER (WHERE flag), " +
            "STDDEV(v) FILTER (WHERE flag), STDDEV_SAMP(v) FILTER (WHERE flag), STDDEV_POP(v) FILTER (WHERE flag), " +
            "VARIANCE(v) FILTER (WHERE flag), VAR_SAMP(v) FILTER (WHERE flag), VAR_POP(v) FILTER (WHERE flag) " +
            "FROM t" + grouping + " HAVING COUNT(*) FILTER (WHERE flag) = 2 ORDER BY SUM(v) FILTER (WHERE flag);");

        // Assert
        var row = rows.ShouldHaveSingleItem();
        row.Take(11).ShouldBe(new object?[] { 2L, 2L, 6m, 3m, 2, 4, "a,b", "a,b", true, true, true });
        row.Skip(11).ShouldBe(new object?[] { 1.4142135623730950488016887242m, 1.4142135623730950488016887242m, 1m, 2m, 2m, 1m });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - FILTER: rejected rows never evaluate application arguments or ordering")]
    public async Task Filter_RejectedInputs_ShouldAvoidSideEffectsAndRetainSeparateSlots()
    {
        // Arrange
        int calls = 0;
        int predicates = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("filter-effects");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => { calls++; return value; }));
        builder.Functions.Add(SqlScalarFunction.Create<int, bool>("flip", _ => ++predicates % 2 == 1));
        builder.Functions.Add(SqlAggregateFunction.Create<int, int, int>("custom_sum", static () => 0, static (state, value) => state + value, static state => state));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("filter");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(0),(1),(2);");

        // Act
        var rows = await RowsAsync(session,
            "SELECT custom_sum(DISTINCT observe(id) ORDER BY observe(id)) FILTER (WHERE id > 0), " +
            "SUM(observe(id)) FILTER (WHERE NULL), SUM(id) FILTER (WHERE id=1), SUM(id) FILTER (WHERE id=2), " +
            "COUNT(*) FILTER (WHERE FALSE) FROM t;");

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 3, null, 1m, 2m, 0L });
        calls.ShouldBe(4);
        (await RowsAsync(session, "SELECT COUNT(*) FILTER (WHERE flip(id)), COUNT(*) FILTER (WHERE flip(id)) FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 3L, 0L });
        predicates.ShouldBe(6);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - FILTER: subqueries and enclosing columns bind in the input scope")]
    public async Task Filter_CorrelatedConditions_ShouldPrepareOnlyAdmittedArguments()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("filter");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(0),(1),(2);");

        // Act
        var rows = await RowsAsync(session,
            "SELECT o.id, (SELECT SUM((SELECT 8/i.id)) FILTER (WHERE i.id > o.id AND " +
            "EXISTS (SELECT 1 WHERE i.id > 0)) FROM t i) FROM t o ORDER BY o.id;");
        var nested = await RowsAsync(session, "SELECT SUM(id) FILTER (WHERE (SELECT COUNT(*) FROM t) > 0) FROM t;");
        var rejected = await RowsAsync(session, "SELECT SUM((SELECT 1/0)) FILTER (WHERE FALSE), " +
            "STRING_AGG('a', ',' ORDER BY (SELECT 1/0)) FILTER (WHERE NULL) FROM t;");

        // Assert
        CheckRows(rows, [[0, 12m], [1, 4m], [2, null]]);
        nested.ShouldHaveSingleItem().ShouldBe(new object?[] { 3m });
        rejected.ShouldHaveSingleItem().ShouldBe(new object?[] { null, null });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FILTER: invalid placements retain structured diagnostics")]
    [InlineData("SELECT UPPER('a') FILTER (WHERE TRUE);", "CS05072")]
    [InlineData("SELECT COALESCE(1,2) FILTER (WHERE TRUE);", "CS05072")]
    [InlineData("SELECT merge_action() FILTER (WHERE TRUE);", "CS05072")]
    [InlineData("SELECT SUM(id) FILTER (WHERE COUNT(*) > 0) FROM t;", "CS05073")]
    [InlineData("SELECT SUM(id) FILTER (WHERE 1) FROM t;", "CS02003")]
    [InlineData("SELECT SUM(id) FILTER (WHERE ROW_NUMBER() > 0) FROM t;", "CS05076")]
    [InlineData("SELECT SUM(id) FILTER (WHERE RANK() OVER()>0) OVER () FROM t;", "CS05077")]
    [InlineData("SELECT BOOL_AND(id) FROM t;", "CS01006")]
    [InlineData("SELECT VAR_POP('text') FROM t;", "CS01006")]
    public async Task Filter_InvalidPlacement_ShouldRefuseBeforeInput(string statement, string code)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("filter");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, statement));

        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Boolean aggregates: NULLs and empty inputs follow PostgreSQL")]
    [InlineData("", false, true)]
    [InlineData(" WHERE id=1", true, true)]
    [InlineData(" WHERE id=2", false, false)]
    [InlineData(" WHERE id=3", null, null)]
    [InlineData(" WHERE FALSE", null, null)]
    public async Task BooleanAggregates_NullAndEmptyInputs_ShouldReturnNullableBoolean(string where, bool? and, bool? or)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("boolean");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT, b BOOLEAN);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,TRUE),(2,FALSE),(3,NULL);");

        // Act
        await using var result = (await ExecuteAsync(session, "SELECT BOOL_AND(b), EVERY(b), BOOL_OR(b) FROM t" + where + ";")).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.ShouldAllBe(column => column.Type == DatabaseType.Boolean && column.IsNullable);
        (await ReadRowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { and, and, or });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Statistics: exact and floating input types retain their result domain")]
    [InlineData("SMALLINT", DatabaseType.Decimal)]
    [InlineData("INT", DatabaseType.Decimal)]
    [InlineData("BIGINT", DatabaseType.Decimal)]
    [InlineData("NUMERIC", DatabaseType.Decimal)]
    [InlineData("REAL", DatabaseType.Float64)]
    [InlineData("DOUBLE PRECISION", DatabaseType.Float64)]
    public async Task Statistics_TypedInputs_ShouldPreserveMetadataAndBoundaryValues(string type, DatabaseType expected)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("statistics");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE t(v {type});");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1),(3),(NULL);");

        foreach (string where in new[] { "", " WHERE v=1", " WHERE FALSE", " WHERE v IS NULL" })
        {
            // Act
            await using var result = (await ExecuteAsync(session,
                "SELECT VAR_SAMP(v), VARIANCE(v), VAR_POP(v), STDDEV(v), STDDEV_SAMP(v), STDDEV_POP(v) FROM t" + where + ";")).ShouldBeAssignableTo<DatabaseResultSet>();

            // Assert
            result.Columns.ShouldAllBe(column => column.Type == expected && column.IsNullable);
            var row = (await ReadRowsAsync(result)).ShouldHaveSingleItem();
            if (where is " WHERE FALSE" or " WHERE v IS NULL") { row.ShouldAllBe(value => value == null); }
            else if (where == " WHERE v=1")
            {
                row[0].ShouldBeNull(); row[1].ShouldBeNull(); row[3].ShouldBeNull(); row[4].ShouldBeNull();
                Convert.ToDecimal(row[2]).ShouldBe(0m); Convert.ToDecimal(row[5]).ShouldBe(0m);
            }
            else
            {
                Convert.ToDecimal(row[0]).ShouldBe(2m); row[0].ShouldBe(row[1]); row[3].ShouldBe(row[4]);
                Convert.ToDecimal(row[2]).ShouldBe(1m); Convert.ToDecimal(row[5]).ShouldBe(1m);
                if (expected == DatabaseType.Decimal) { row[3].ShouldBe(1.4142135623730950488016887242m); }
                else { row[3].ShouldBe(Math.Sqrt(2)); }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Statistics: exact accumulation avoids overflow and cancellation rounding")]
    public async Task Statistics_ExactBoundaries_ShouldRoundOnlyFinalResults()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("statistics");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(v DECIMAL);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(9223372036854775806),(9223372036854775807);");
        await ExecuteAsync(session, "CREATE TABLE integer_limits(v BIGINT);");
        await ExecuteAsync(session, "INSERT INTO integer_limits VALUES(@low),(@high);",
            new Dictionary<string, object?> { ["low"] = long.MinValue, ["high"] = long.MaxValue });

        // Act / Assert
        (await RowsAsync(session, "SELECT STDDEV_POP(v) FROM integer_limits;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 9223372036854775807.5m });
        (await RowsAsync(session, "SELECT VAR_SAMP(v), VAR_POP(v), STDDEV_POP(v) FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 0.5m, 0.25m, 0.5m });
        await ExecuteAsync(session, "DELETE FROM t;");
        await ExecuteAsync(session, "INSERT INTO t VALUES(0.1000000000000000000000000001),(0.1000000000000000000000000003);");
        (await RowsAsync(session, "SELECT VAR_POP(v), STDDEV_POP(v) FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 0m, 0.0000000000000000000000000001m });
        await ExecuteAsync(session, "DELETE FROM t;");
        await ExecuteAsync(session, "INSERT INTO t VALUES(0.0000000000000000000000000000),(0.0000000000000000000000000001);");
        (await RowsAsync(session, "SELECT STDDEV_POP(v) FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 0.0000000000000000000000000001m });
        await ExecuteAsync(session, "DELETE FROM t;");
        await ExecuteAsync(session, "INSERT INTO t VALUES(@v),(@v);", new Dictionary<string, object?> { ["v"] = decimal.MaxValue });
        (await RowsAsync(session, "SELECT VAR_SAMP(v), STDDEV_POP(v) FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 0m, 0m });
        (await RowsAsync(session, "SELECT VAR_POP(CASE WHEN id=1 THEN 1 ELSE CAST(3 AS NUMERIC) END) FROM (SELECT 1 AS id UNION ALL SELECT 2) s;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1m });
        (await RowsAsync(session, "SELECT VAR_POP(CASE WHEN id=1 THEN 1 ELSE CAST(3 AS DOUBLE PRECISION) END) FROM (SELECT 1 AS id UNION ALL SELECT 2) s;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1d });
        (await RowsAsync(session, "SELECT VAR_POP(COALESCE(CASE WHEN id=1 THEN 1 END, CAST(3 AS DOUBLE PRECISION))) FROM (SELECT 1 AS id UNION ALL SELECT 2) s;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1d });
        (await RowsAsync(session, "SELECT VAR_POP(CASE WHEN id=1 THEN CAST(1 AS NUMERIC) ELSE CAST(3 AS DOUBLE PRECISION) END) FROM (SELECT 1 AS id UNION ALL SELECT 2) s;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1d });
        const string realChoice = "CASE WHEN id=1 THEN 16777217 ELSE CAST(16777216 AS REAL) END";
        (await RowsAsync(session, $"SELECT VAR_SAMP(DISTINCT {realChoice}), VAR_POP({realChoice}) FROM (SELECT 1 AS id UNION ALL SELECT 2) s;")).ShouldHaveSingleItem().ShouldBe(new object?[] { null, 0d });
        await ExecuteAsync(session, "DELETE FROM t;");
        await ExecuteAsync(session, "INSERT INTO t VALUES(@low),(@high);", new Dictionary<string, object?> { ["low"] = long.MinValue, ["high"] = long.MaxValue });
        (await RowsAsync(session, "SELECT STDDEV_POP(v) FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { 9223372036854775807.5m });
        var overflow = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT VAR_POP(v) FROM t;"));
        overflow.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS02002");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Statistics: nonfinite inputs and untyped NULL follow floating overloads")]
    public async Task Statistics_NonfiniteAndNullInputs_ShouldRetainPostgreSqlResults()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("statistics");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(v DOUBLE PRECISION);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(@v);", new Dictionary<string, object?> { ["v"] = double.PositiveInfinity });

        // Act
        var row = (await RowsAsync(session, "SELECT VAR_POP(v), STDDEV_POP(v), VAR_SAMP(v) FROM t;")).ShouldHaveSingleItem();
        await using var unknown = (await ExecuteAsync(session, "SELECT VAR_POP(NULL);")).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        double.IsNaN((double)row[0]!).ShouldBeTrue(); double.IsNaN((double)row[1]!).ShouldBeTrue(); row[2].ShouldBeNull();
        unknown.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Float64);
        (await ReadRowsAsync(unknown)).ShouldHaveSingleItem()[0].ShouldBeNull();
    }
}
