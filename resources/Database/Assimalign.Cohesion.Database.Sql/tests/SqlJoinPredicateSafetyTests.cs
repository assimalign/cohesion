using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Preserves deferred join errors while proving safe input filters still run early.</summary>
public sealed class SqlJoinPredicateSafetyTests
{
    /// <summary>Skipped rows do not evaluate unsafe predicates, while reached faults still surface.</summary>
    /// <param name="predicate">A predicate that fails on at least one discarded row.</param>
    /// <returns>A task representing the test.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: throwing WHERE terms retain joined-row evaluation (#1521)")]
    [InlineData("CAST(a.s AS INT) > 0")]
    [InlineData("10 / a.d > 1")]
    [InlineData("a.n + 1 > 0")]
    [InlineData("CAST(a.n AS INT) > 0")]
    [InlineData("CAST(a.n AS DECIMAL(2,0)) > 0")]
    [InlineData("ABS(a.n) > 0")]
    [InlineData("a.s LIKE a.pattern")]
    public async Task Join_ThrowingWhereTerm_ShouldSkipFilteredAndUnmatchedRows(string predicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-errors", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT, s TEXT, d INT, n BIGINT, pattern TEXT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT, kind TEXT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1,'1',2,2,'%'),(2,'x',0,9223372036854775807,'x\\'),(3,'x',0,-9223372036854775808,'x\\')");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1,'n'),(2,'s')");
        string sql = $"SELECT a.id FROM a JOIN b ON a.id=b.id WHERE b.kind='n' AND {predicate}";

        // Act
        var plan = Plan(database, sql);
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1 });
        plan.Inputs[0].Filter.ShouldBeNull();
        plan.Inputs[1].Filter.ShouldNotBeNull();

        // A reached fault must still surface at the residual.
        await ExecuteAsync(session, "UPDATE b SET kind='n';");
        await ExecuteAsync(session, "INSERT INTO b VALUES (3,'n')");
        await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, sql));
    }

    /// <summary>A nullable-side ON division runs only after its key matches.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: nullable-side ON division retains key short-circuit (#1521)")]
    public async Task Join_NullableOnDivision_ShouldSkipUnmatchedZeroDivisor()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-on-errors", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT, d INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1),(2)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1,2),(3,0)");
        const string sql = "SELECT a.id,b.d FROM a LEFT JOIN b ON a.id=b.id AND 10/b.d>1 ORDER BY a.id";

        // Act
        var plan = Plan(database, sql);
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.Select(row => string.Join("|", row)).ShouldBe(["1|2", "2|"]);
        plan.Inputs[1].Filter.ShouldBeNull();
    }

    /// <summary>Immutable application metadata does not prove a call unable to throw.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: immutable application calls retain deferred errors (#1521)")]
    public async Task Join_ImmutableCall_ShouldRemainAtOriginalLevel()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("join-call-errors");
        builder.Functions.Add(SqlScalarFunction.Create<long, long>("checked_divide", value =>
        {
            calls++;
            return 10 / value;
        }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT, d BIGINT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1,2),(2,0)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1)");
        const string sql = "SELECT a.id FROM a JOIN b ON a.id=b.id WHERE checked_divide(a.d)>1";

        // Act
        var rows = await RowsAsync(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1 });
        calls.ShouldBe(1);
        Plan(database, sql).Inputs[0].Filter.ShouldBeNull();
    }

    /// <summary>Safe restrictions reduce pair evaluations from 4,096 to one without indexes.</summary>
    /// <param name="predicate">A safe single-input restriction.</param>
    /// <returns>A task representing the test.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: proven safe terms still reduce pair evaluations (#1521)")]
    [InlineData("a.id=@id")]
    [InlineData("a.id BETWEEN @id AND @id")]
    [InlineData("a.id IN (@id,NULL)")]
    [InlineData("CAST(a.id AS BIGINT)=@id")]
    [InlineData("a.name LIKE @pattern")]
    [InlineData("a.name COLLATE case_insensitive='SEVEN'")]
    [InlineData("LENGTH(a.name)=5")]
    [InlineData("a.marker IS NULL")]
    [InlineData("a.marker IS NOT NULL AND a.id=@id")]
    public async Task Join_SafeRestriction_ShouldEvaluateOnlyFilteredPairs(string predicate)
    {
        // Arrange
        int pairs = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("join-safe");
        builder.Functions.Add(SqlScalarFunction.Create("pair_seen", (long left, long right) => { pairs++; return left + right; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT, name TEXT, marker INT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT)");
        bool notNull = predicate.Contains("IS NOT NULL", StringComparison.Ordinal);
        await ExecuteAsync(session, "INSERT INTO a VALUES " + string.Join(",", Enumerable.Range(0, 64)
            .Select(id => $"({id},'{(id == 7 ? "seven" : "other-name")}',{(id == 7 && !notNull ? "NULL" : "1")})")));
        await ExecuteAsync(session, "INSERT INTO b VALUES " + string.Join(",", Enumerable.Range(0, 64).Select(id => $"({id})")));
        var parameters = new Dictionary<string, object?> { ["id"] = 7, ["pattern"] = "sev%" };
        string sql = $"SELECT a.id,b.id FROM a JOIN b ON pair_seen(a.id,b.id)>=0 WHERE {predicate} AND b.id=@id";

        // Act
        var rows = await RowsAsync(session, sql, parameters);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 7, 7 });
        pairs.ShouldBe(1);
        session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined.ShouldBe(128);
        Plan(database, sql, parameters).Inputs.ShouldAllBe(input => input.Filter != null);
    }

    /// <summary>Clock-based seek bounds and input filters retain the instant captured at BEGIN.</summary>
    /// <param name="type">The clock value's SQL type.</param>
    /// <param name="current">The datetime value expression.</param>
    /// <returns>A task representing the test.</returns>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: clock seek bounds and runtime filters share the transaction instant (#1521)")]
    [InlineData("TIMESTAMPTZ", "NOW()")]
    [InlineData("TIMESTAMPTZ", "CURRENT_TIMESTAMP")]
    [InlineData("TIMESTAMP", "LOCALTIMESTAMP")]
    [InlineData("DATE", "CURRENT_DATE")]
    [InlineData("TIME", "CURRENT_TIME")]
    [InlineData("TIME", "LOCALTIME")]
    public async Task Join_ClockRestriction_ShouldShareSeekAndFilterInstant(string type, string current)
    {
        // Arrange
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        await using var engine = SqlDatabaseEngine.Create("join-clock", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE a (id INT, ts {type})");
        await ExecuteAsync(session, "CREATE TABLE b (id INT)");
        await ExecuteAsync(session, $"INSERT INTO a VALUES (1,{current})");
        clock.Instant += TimeSpan.FromHours(25);
        await ExecuteAsync(session, $"INSERT INTO a VALUES (2,{current})");
        clock.Instant = SqlT1ScalarTests.Instant;
        await ExecuteAsync(session, "INSERT INTO b VALUES (1)");
        await ExecuteAsync(session, "CREATE INDEX ix_clock ON a(ts)");
        await session.BeginTransactionAsync(CancellationToken.None);
        clock.Instant += TimeSpan.FromHours(25);
        string sql = $"SELECT a.id,{current} FROM a JOIN b ON a.id=b.id WHERE a.ts={current}";

        // Act
        var seek = await RowsAsync(session, sql);
        long examined = session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined;
        await ExecuteAsync(session, "COMMIT");
        clock.Instant = SqlT1ScalarTests.Instant;
        await ExecuteAsync(session, "DROP INDEX ix_clock ON a");
        var scan = await RowsAsync(session, sql);

        // Assert
        seek.ShouldHaveSingleItem()[0].ShouldBe(1);
        scan.Select(row => string.Join("|", row)).ShouldBe(seek.Select(row => string.Join("|", row)));
        examined.ShouldBe(2);
        session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined.ShouldBe(3);
    }

    private static SqlJoinPlan Plan(SqlDatabase database, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => new SqlPlanner(database.Catalog, parameters, database.Definitions.Functions)
            .Plan(SqlQueryRequest.FromSql(sql, parameters).Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
        => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(sql, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
