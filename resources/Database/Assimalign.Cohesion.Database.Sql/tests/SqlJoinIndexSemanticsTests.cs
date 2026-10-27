using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Verifies that join index probes preserve evaluator equality: floating keys,
/// whose physical identity is finer than SQL comparison, scan; temporal keys,
/// whose identity matches it, seek.
/// </summary>
public sealed class SqlJoinIndexSemanticsTests
{
    /// <summary>Adjacent floating values remain distinct under conservative join scanning.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: floating equality preserves adjacent double distinctions")]
    public async Task Join_CloseDoubleKeys_ShouldScanWithoutCollapsingDistinctRows()
    {
        // Arrange: both comparison and keys preserve adjacent IEEE values;
        // joins still scan because signed-zero equality spans distinct keys.
        double left = 1d;
        double right = Math.BitIncrement(left);
        BitConverter.DoubleToInt64Bits(left).ShouldNotBe(BitConverter.DoubleToInt64Bits(right));

        // Act + Assert.
        await AssertUnsafeEqualityScansAsync("DOUBLE", left, right, equal: false);
    }

    /// <summary>Probes a timestamp index with equal ticks written in every DateTime kind.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: timestamp probes seek every DateTime kind with equal ticks (#1099)")]
    public async Task Join_DateTimeKinds_ShouldSeekWithoutLosingEqualRows()
    {
        // Arrange: DateTime comparison uses ticks, and index keys encode the ticks alone.
        var outer = new DateTime(2026, 9, 18, 12, 30, 0, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(outer, DateTimeKind.Unspecified);
        var local = DateTime.SpecifyKind(outer, DateTimeKind.Local);
        var other = DateTime.SpecifyKind(outer.AddTicks(1), DateTimeKind.Utc);

        // Act + Assert.
        await AssertTemporalEqualitySeeksAsync("TIMESTAMP", outer, unspecified, local, other);
    }

    /// <summary>Probes a timestamp-offset index with one instant written at different offsets.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: timestamp-offset probes seek the same instant at every offset (#1099)")]
    public async Task Join_DateTimeOffsets_ShouldSeekWithoutLosingEqualRows()
    {
        // Arrange: one instant at three offsets; index keys encode the instant alone.
        var outer = new DateTimeOffset(2026, 9, 18, 12, 30, 0, TimeSpan.Zero);
        var east = outer.ToOffset(TimeSpan.FromHours(3));
        var west = outer.ToOffset(TimeSpan.FromHours(-5.5));
        var other = outer.AddMinutes(1).ToOffset(TimeSpan.FromHours(3));

        // Act + Assert.
        await AssertTemporalEqualitySeeksAsync("TIMESTAMPTZ", outer, east, west, other);
    }

    /// <summary>Skips decimal probes that cannot equal any indexed Int32 value.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: decimal-to-integer probes preserve exact matches and reject rounded overflow and null keys")]
    public async Task Join_DecimalProbesIntoIntegerIndex_ShouldPreserveExactEquality()
    {
        // Arrange: 1.5 rounds during ordinary storage coercion, and the two
        // large keys overflow Int32. None can equal a stored integer.
        await using var engine = SqlDatabaseEngine.Create("join-decimal-probes", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("join-decimal-probes");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE outer_rows (id INT, key_value DECIMAL)");
        await ExecuteAsync(session, "CREATE TABLE inner_rows (id INT, key_value INT)");
        await ExecuteAsync(session, "CREATE INDEX ix_key ON inner_rows (key_value)");
        await ExecuteAsync(session, "INSERT INTO outer_rows VALUES (1, 1.000), (2, 1.5), (3, 2147483648), (4, -2147483649), (5, NULL), (6, 2.000)");
        await ExecuteAsync(session, "INSERT INTO inner_rows VALUES (10, 1), (20, 2), (30, NULL)");
        const string sql = "SELECT l.id, r.id FROM outer_rows l INNER JOIN inner_rows r ON l.key_value = r.key_value ORDER BY l.id, r.id";

        // Act.
        var plan = PlanOf(database, sql);
        var pairs = await ReadPairsAsync(session, sql);

        // Assert: a rounded probe must not manufacture a match, overflow must
        // not abort the statement, and NULL must not match the indexed NULL.
        plan.Steps[0].Access.ShouldNotBeNull().Index.Name.ShouldBe("ix_key");
        pairs.ShouldBe(new[] { (1, 10), (6, 20) });
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-seek:ix_key"]);
    }

    /// <summary>Probes a decimal index with integer values without losing normalization.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: integer-to-decimal probes retain normalized equal values and duplicates")]
    public async Task Join_IntegerProbesIntoDecimalIndex_ShouldMatchNormalizedKeys()
    {
        // Arrange.
        await using var engine = SqlDatabaseEngine.Create("join-integer-probes", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("join-integer-probes");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE outer_rows (id INT, key_value INT)");
        await ExecuteAsync(session, "CREATE TABLE inner_rows (id INT, key_value DECIMAL)");
        await ExecuteAsync(session, "CREATE INDEX ix_key ON inner_rows (key_value)");
        await ExecuteAsync(session, "INSERT INTO outer_rows VALUES (1, 1), (2, 2), (3, NULL)");
        await ExecuteAsync(session, "INSERT INTO inner_rows VALUES (10, 1.0), (11, 1.000), (12, 1.5), (20, 2.000), (30, NULL)");
        const string sql = "SELECT l.id, r.id FROM outer_rows l INNER JOIN inner_rows r ON l.key_value = r.key_value ORDER BY l.id, r.id";

        // Act.
        var plan = PlanOf(database, sql);
        var pairs = await ReadPairsAsync(session, sql);

        // Assert.
        plan.Steps[0].Access.ShouldNotBeNull().Index.Name.ShouldBe("ix_key");
        pairs.ShouldBe(new[] { (1, 10), (1, 11), (2, 20) });
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-seek:ix_key"]);
    }

    /// <summary>Uses only mandatory key equalities and still evaluates all residual predicates.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: OR alternatives scan while mandatory AND equalities seek with the full residual")]
    public async Task Join_OptionalAndMandatoryEqualities_ShouldChooseSoundAccessPaths()
    {
        // Arrange: row 20 matches only the OR alternative; row 40 matches the
        // key equality but fails the AND query's residual predicate.
        await using var engine = SqlDatabaseEngine.Create("join-boolean-paths", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("join-boolean-paths");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE outer_rows (id INT, key_value INT, marker INT)");
        await ExecuteAsync(session, "CREATE TABLE inner_rows (id INT, key_value INT, marker INT)");
        await ExecuteAsync(session, "CREATE INDEX ix_key ON inner_rows (key_value)");
        await ExecuteAsync(session, "INSERT INTO outer_rows VALUES (1, 1, 100), (2, 2, 200)");
        await ExecuteAsync(session, "INSERT INTO inner_rows VALUES (10, 1, 100), (20, 7, 200), (30, 2, 0), (40, 2, 300)");
        const string optionalEquality = "SELECT l.id, r.id FROM outer_rows l INNER JOIN inner_rows r ON l.key_value = r.key_value OR l.marker = r.marker ORDER BY l.id, r.id";
        const string mandatoryEquality = "SELECT l.id, r.id FROM outer_rows l INNER JOIN inner_rows r ON l.key_value = r.key_value AND (r.marker = 0 OR l.marker = r.marker) ORDER BY l.id, r.id";

        // Act + Assert.
        PlanOf(database, optionalEquality).Steps[0].Access.ShouldBeNull();
        (await ReadPairsAsync(session, optionalEquality)).ShouldBe(new[] { (1, 10), (2, 20), (2, 30), (2, 40) });
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-scan"]);

        PlanOf(database, mandatoryEquality).Steps[0].Access.ShouldNotBeNull().Index.Name.ShouldBe("ix_key");
        (await ReadPairsAsync(session, mandatoryEquality)).ShouldBe(new[] { (1, 10), (2, 30) });
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-seek:ix_key"]);
    }

    /// <summary>A retained plan never probes a replacement index with the previous key shape.</summary>
    /// <param name="replaced">Whether the dropped name is reused for a different key.</param>
    /// <param name="reverse">Whether the indexed input is written first and the plan swaps it.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: detached and replaced indexes fall back to the full residual (#1521)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Join_DetachedOrReplacedIndex_ShouldPreserveMatches(bool replaced, bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-replaced", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT, marker INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1), (NULL)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1, 0), (1, 0), (2, 0), (NULL, 0)");
        await ExecuteAsync(session, "CREATE INDEX ix ON b(id)");
        var plan = PlanOf(database, reverse
            ? "SELECT a.id, b.id FROM b JOIN a ON a.id = b.id"
            : "SELECT a.id, b.id FROM a JOIN b ON a.id = b.id");
        plan.FirstBinding.ShouldBe(reverse ? 1 : 0);
        plan.Steps[0].Access.ShouldNotBeNull().Index.Name.ShouldBe("ix");
        await ExecuteAsync(session, "DROP INDEX ix ON b");
        if (replaced) { await ExecuteAsync(session, "CREATE INDEX ix ON b(marker)"); }
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            var statement = new SqlStatementContext(transaction, database.Coordinator);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            await using var result = (await executor.ExecuteAsync(plan, statement, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            var pairs = new List<(int, int)>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                pairs.Add((row.GetValue(0).ShouldBeOfType<int>(), row.GetValue(1).ShouldBeOfType<int>()));
            }

            // Assert
            pairs.ShouldBe([(1, 1), (1, 1)]);
            statement.Metrics.JoinAccessPaths.ShouldBe(["join-scan"]);
            statement.Metrics.RecordsExamined.ShouldBe(6);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    /// <summary>Exact constants cross numeric equijoins without rounding, NULL matches or lost duplicates.</summary>
    /// <param name="key">The bound decimal key; null exercises SQL UNKNOWN.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - JOIN: propagated constants preserve mixed numeric and NULL semantics (#1521)")]
    [InlineData("1.000")]
    [InlineData("1.5")]
    [InlineData(null)]
    public async Task Join_PropagatedNumericConstant_ShouldPreserveScanDigest(string? key)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-constants", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT, k DECIMAL)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT, k INT)");
        await ExecuteAsync(session, "CREATE TABLE c (id INT, k BIGINT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1,1.000),(2,1.5),(3,NULL)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (10,1),(11,1),(20,2),(30,NULL)");
        await ExecuteAsync(session, "INSERT INTO c VALUES (100,1),(101,1),(200,2),(300,NULL)");
        await ExecuteAsync(session, "CREATE INDEX ix_b ON b(k)");
        await ExecuteAsync(session, "CREATE INDEX ix_c ON c(k)");
        var parameters = new Dictionary<string, object?> { ["key"] = key is null ? null : decimal.Parse(key, System.Globalization.CultureInfo.InvariantCulture) };
        const string sql = "SELECT b.id,c.id FROM a JOIN b ON a.k=b.k JOIN c ON b.k=c.k WHERE a.k=@key ORDER BY b.id,c.id";
        var request = SqlQueryRequest.FromSql(sql, parameters);
        var plan = new SqlPlanner(database.Catalog, parameters).Plan(request.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();

        // Act
        var seek = await PairsAsync();
        await ExecuteAsync(session, "DROP INDEX ix_b ON b");
        await ExecuteAsync(session, "DROP INDEX ix_c ON c");
        var scan = await PairsAsync();

        // Assert
        seek.ShouldBe(scan);
        if (key == "1.000")
        {
            seek.ShouldBe([(10,100),(10,101),(11,100),(11,101)]);
            plan.Inputs[1].Access.ShouldBeOfType<SqlIndexSeekPath>().EqualityValues.ShouldBe(new object?[] { 1 });
            plan.Inputs[2].Access.ShouldBeOfType<SqlIndexSeekPath>().EqualityValues.ShouldBe(new object?[] { 1L });
        }
        else
        {
            seek.ShouldBeEmpty();
            plan.Inputs[1].Access.ShouldBeOfType<SqlScanPath>();
        }

        async Task<(int, int)[]> PairsAsync()
        {
            await using var result = (await ExecuteAsync(session, sql, parameters)).ShouldBeAssignableTo<DatabaseResultSet>();
            var rows = new List<(int, int)>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                rows.Add((row.GetValue(0).ShouldBeOfType<int>(), row.GetValue(1).ShouldBeOfType<int>()));
            }
            return rows.ToArray();
        }
    }

    /// <summary>Text constants cannot cross incompatible equality collations.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: collation boundaries keep constant inference conservative (#1521)")]
    public async Task Join_DifferentCollations_ShouldPreserveCaseInsensitiveMatches()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-collation-constants", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT, k TEXT COLLATE binary)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT, k TEXT COLLATE binary)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1,'a')");
        await ExecuteAsync(session, "INSERT INTO b VALUES (10,'A'),(11,'a')");
        await ExecuteAsync(session, "CREATE INDEX ix_b ON b(k)");
        const string sql = "SELECT a.id,b.id FROM a JOIN b ON a.k=b.k COLLATE case_insensitive WHERE a.k='a' ORDER BY b.id";

        // Act
        var plan = PlanOf(database, sql);
        var pairs = await ReadPairsAsync(session, sql);

        // Assert
        pairs.ShouldBe([(1,10),(1,11)]);
        plan.Inputs[1].Access.ShouldBeOfType<SqlScanPath>();
        plan.Inputs[1].Filter.ShouldBeNull();
    }

    /// <summary>A constant seek on the driver obeys the same catalog identity guard as a correlated seek.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - JOIN: replaced driving index falls back without losing rows (#1521)")]
    public async Task Join_ReplacedDrivingIndex_ShouldPreserveMatches()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-replaced-driver", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (id INT, marker INT)");
        await ExecuteAsync(session, "CREATE TABLE b (id INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1,0),(2,0)");
        await ExecuteAsync(session, "INSERT INTO b VALUES (1),(1),(2)");
        await ExecuteAsync(session, "CREATE UNIQUE INDEX ix ON a(id)");
        var plan = PlanOf(database, "SELECT a.id,b.id FROM a JOIN b ON a.id=b.id WHERE a.id=1");
        plan.FirstBinding.ShouldBe(0);
        plan.Inputs[0].Access.ShouldBeOfType<SqlIndexSeekPath>();
        await ExecuteAsync(session, "DROP INDEX ix ON a");
        await ExecuteAsync(session, "CREATE INDEX ix ON a(marker)");
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        try
        {
            var statement = new SqlStatementContext(transaction, database.Coordinator);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            await using var result = (await executor.ExecuteAsync(plan, statement, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                row.GetValue(0).ShouldBe(1);
                row.GetValue(1).ShouldBe(1);
                count++;
            }

            // Assert
            count.ShouldBe(2);
            statement.Metrics.RecordsExamined.ShouldBe(5);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    private static async Task AssertUnsafeEqualityScansAsync(string type, object left, object right, bool equal = true)
    {
        (SqlExpressionEvaluator.Compare(left, right) == 0).ShouldBe(equal);
        await using var engine = SqlDatabaseEngine.Create("join-equality-fallback", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("join-equality-fallback");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE outer_rows (id INT, key_value {type})");
        await ExecuteAsync(session, $"CREATE TABLE inner_rows (id INT, key_value {type})");
        await ExecuteAsync(session, "CREATE INDEX ix_key ON inner_rows (key_value)");
        await ExecuteAsync(session, "INSERT INTO outer_rows VALUES (1, @left)", new Dictionary<string, object?> { ["left"] = left });
        await ExecuteAsync(session, "INSERT INTO inner_rows VALUES (2, @left), (3, @right)",
            new Dictionary<string, object?> { ["left"] = left, ["right"] = right });
        const string sql = "SELECT l.id, r.id FROM outer_rows l INNER JOIN inner_rows r ON l.key_value = r.key_value ORDER BY l.id, r.id";

        PlanOf(database, sql).Steps[0].Access.ShouldBeNull();
        (await ReadPairsAsync(session, sql)).ShouldBe(equal ? new[] { (1, 2), (1, 3) } : new[] { (1, 2) });
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-scan"]);
    }

    private static async Task AssertTemporalEqualitySeeksAsync(string type, object outer, object first, object second, object other)
    {
        SqlExpressionEvaluator.Compare(outer, first).ShouldBe(0);
        SqlExpressionEvaluator.Compare(outer, second).ShouldBe(0);
        SqlExpressionEvaluator.Compare(outer, other).ShouldNotBe(0);
        await using var engine = SqlDatabaseEngine.Create("join-temporal-seek", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("join-temporal-seek");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE outer_rows (id INT, key_value {type})");
        await ExecuteAsync(session, $"CREATE TABLE inner_rows (id INT, key_value {type})");
        await ExecuteAsync(session, $"CREATE TABLE scan_rows (id INT, key_value {type})");
        await ExecuteAsync(session, "CREATE INDEX ix_key ON inner_rows (key_value)");
        var values = new Dictionary<string, object?> { ["outer"] = outer, ["first"] = first, ["second"] = second, ["other"] = other };
        await ExecuteAsync(session, "INSERT INTO outer_rows VALUES (1, @outer)", values);
        await ExecuteAsync(session, "INSERT INTO inner_rows VALUES (2, @first), (3, @second), (4, @other), (5, NULL)", values);
        await ExecuteAsync(session, "INSERT INTO scan_rows VALUES (2, @first), (3, @second), (4, @other), (5, NULL)", values);
        const string seek = "SELECT l.id, r.id FROM outer_rows l INNER JOIN inner_rows r ON l.key_value = r.key_value ORDER BY l.id, r.id";
        const string scan = "SELECT l.id, r.id FROM outer_rows l INNER JOIN scan_rows r ON l.key_value = r.key_value ORDER BY l.id, r.id";

        // The unindexed hash path retains the complete evaluator equality as residual.
        PlanOf(database, scan).Steps[0].Access.ShouldBeNull();
        var expected = await ReadPairsAsync(session, scan);
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-hash"]);
        expected.ShouldBe(new[] { (1, 2), (1, 3) });

        PlanOf(database, seek).Steps[0].Access.ShouldNotBeNull().Index.Name.ShouldBe("ix_key");
        (await ReadPairsAsync(session, seek)).ShouldBe(expected);
        MetricsOf(session).JoinAccessPaths.ShouldBe(["join-seek:ix_key"]);
    }

    private static SqlJoinPlan PlanOf(SqlDatabase database, string sql)
    {
        var request = SqlQueryRequest.FromSql(sql);
        return new SqlPlanner(database.Catalog, request.Parameters)
            .Plan(request.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
    }

    private static SqlStatementMetrics MetricsOf(SqlDatabaseSession session)
        => session.LastStatementMetrics.ShouldNotBeNull();

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<(int Left, int Right)[]> ReadPairsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        var pairs = new List<(int Left, int Right)>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            pairs.Add((row.GetValue(0).ShouldBeOfType<int>(), row.GetValue(1).ShouldBeOfType<int>()));
        }
        return pairs.ToArray();
    }
}
