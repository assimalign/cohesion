using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDirectedIndexTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Directed indexes: equality prefixes and bounds on each component equal full scans (#1631)")]
    [InlineData(0, 3, 1)]
    [InlineData(3, 0, 2)]
    [InlineData(2, 1, 3)]
    [InlineData(1, 2, 0)]
    public async Task ExecuteAsync_MixedDirectionRanges_ShouldMatchScanAndPhysicalOrder(int first, int second, int third)
    {
        // Arrange: indexes build from populated tables, then ordinary writes maintain them.
        await using var engine = SqlDatabaseEngine.Create("directed-ranges", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "plain" })
        {
            await Execute(session, "CREATE TABLE " + table + "(id INT,a INT,b INT,c INT,n TEXT COLLATE case_insensitive)");
            int id = 0;
            var rows = new List<string>();
            foreach (var (a, key, c) in
                from a in new[] { "NULL", "-2147483648", "-1", "0", "1", "2147483647" }
                from key in new[] { "NULL", "-1", "0", "1" }
                from c in new[] { "NULL", "-1", "0", "1" } select (a, key, c))
            { rows.Add("(" + id++ + "," + a + "," + key + "," + c + ",'p" + (id % 3) + "')"); }
            await Execute(session, "INSERT INTO " + table + " VALUES" + string.Join(',', rows));
        }
        string order = "a" + Suffix(first) + ",b" + Suffix(second) + ",c" + Suffix(third) + ",n DESC NULLS LAST,id";
        await Execute(session, "CREATE INDEX ix ON t(" + order + ")");
        foreach (string table in new[] { "t", "plain" })
        { await Execute(session, "INSERT INTO " + table + " VALUES(999,0,0,0,'P99'),(1000,0,0,0,NULL)"); }

        // Act / Assert: every prefix, every inclusive/exclusive bound and empty/edge ranges.
        foreach (var (prefix, comparison) in
            from prefix in new[] { "", "a=0 AND ", "a=0 AND b=0 AND " }
            from comparison in new[] { "= 0", "> -1", ">= 0", "< 1", "<= 0", "BETWEEN -1 AND 1", "> 2147483647", "< -2147483648", "> 1 AND {0} < -1" }
            select (prefix, comparison))
        {
            string column = prefix.Length == 0 ? "a" : prefix.Contains("b=", StringComparison.Ordinal) ? "c" : "b";
            string predicate = prefix + column + " " + comparison.Replace("{0}", column, StringComparison.Ordinal);
            await Oracle(database, session, predicate);
        }
        foreach (string predicate in new[] { "a=0 AND b=0 AND c=0 AND n LIKE 'p%'", "a=0 AND b=0 AND c=0 AND n LIKE 'p9%'",
            "a=0 AND b=0 AND c=0 AND n >= 'P1' AND n < 'p9'", "a=0 AND b=0 AND c=0 AND n='P99'" })
        { await Oracle(database, session, predicate); }
        await PhysicalOrder(database, session, order);

        await NullPrefix(database, session, [null], "a IS NULL");
        await NullPrefix(database, session, [0, null], "a=0 AND b IS NULL");

        // A direction changes physical access only: matching and reversed ORDER BY still sort.
        var explain = await Rows(session, "EXPLAIN SELECT id FROM t WHERE a=0 ORDER BY " + order);
        explain.ShouldContain(row => (row[5] as string ?? "").Contains("order_keys=", StringComparison.Ordinal) && !(row[5] as string ?? "").Contains("order_keys=0", StringComparison.Ordinal));
        explain = await Rows(session, "EXPLAIN SELECT id FROM t WHERE a=0 ORDER BY b DESC,c ASC");
        explain.ShouldContain(row => (row[5] as string ?? "").Contains("order_keys=", StringComparison.Ordinal) && !(row[5] as string ?? "").Contains("order_keys=0", StringComparison.Ordinal));

        // UPDATE/DELETE use the same range path; rollback restores directed entries.
        await using (var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None))
        {
            await Execute(session, "UPDATE t SET b=7 WHERE a=0 AND b>=0 AND b<=1");
            await Execute(session, "DELETE FROM t WHERE a=0 AND b=7 AND c>=0");
            await transaction.RollbackAsync(CancellationToken.None);
        }
        await Oracle(database, session, "a=0 AND b>=0 AND b<=1");
        await PhysicalOrder(database, session, order);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Directed uniqueness: declaration order does not change equality or partial NULL rules (#1631)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DirectedUnique_ShouldPreserveConflictAndUndoSemantics(bool partial)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("directed-unique", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,a INT,b TEXT COLLATE case_insensitive,active BOOLEAN)");
        await Execute(session, "CREATE UNIQUE INDEX uq ON t(a DESC NULLS LAST,b ASC NULLS FIRST)" + (partial ? " WHERE active" : ""));
        await Execute(session, "INSERT INTO t VALUES(1,7,'Ab',TRUE),(2,8,'cd',TRUE)");

        // Act / Assert
        await SqlConstraintAssertions.ThrowAsync(() => Execute(session, "INSERT INTO t VALUES(3,7,'AB',TRUE)"));
        await SqlConstraintAssertions.ThrowAsync(() => Execute(session, "UPDATE t SET a=7,b='ab' WHERE id=2"));
        await Execute(session, "INSERT INTO t VALUES(3,7,'AB',TRUE) ON CONFLICT" + (partial ? "" : "(a,b)") + " DO NOTHING");
        (await Rows(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe([1, 2]);
        if (partial) { await Execute(session, "INSERT INTO t VALUES(3,NULL,'Ab',TRUE),(4,NULL,'ab',TRUE)"); }
        await using (var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None))
        {
            await Execute(session, "UPDATE t SET a=9 WHERE a=7 AND b='ab'");
            await Execute(session, "DELETE FROM t WHERE a=8 AND b='CD'");
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await Rows(session, "SELECT id FROM t WHERE a=7 AND b='ab'")).ShouldHaveSingleItem()[0].ShouldBe(1);
        session.LastStatementMetrics!.AccessPath.ShouldBe(partial ? "scan" : "seek:uq");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Directed floating keys: zero signs and NaN payloads preserve SQL unique equality (#1631)")]
    [InlineData("REAL", true)]
    [InlineData("DOUBLE PRECISION", false)]
    public async Task ExecuteAsync_FloatingUnique_ShouldCanonicalizeSqlEquality(string type, bool single)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("directed-floating-unique", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k " + type + ")");
        await Execute(session, "CREATE UNIQUE INDEX ix ON t(k DESC NULLS LAST)");
        object zero = single ? (object)0f : 0d;
        object negativeZero = single ? (object)(-0f) : -0d;
        object nan = single ? (object)float.NaN : double.NaN;
        object otherNan = single ? (object)BitConverter.UInt32BitsToSingle(0x7f800001) : BitConverter.UInt64BitsToDouble(0x7ff0000000000001);
        Task InsertAsync(object value) => session.ExecuteAsync("INSERT INTO t VALUES(@p)",
            new Dictionary<string, object?> { ["p"] = value }, CancellationToken.None).AsTask();

        // Act / Assert
        await InsertAsync(zero);
        await SqlConstraintAssertions.ThrowAsync(() => InsertAsync(negativeZero));
        await Execute(session, "DELETE FROM t");
        await InsertAsync(nan);
        await SqlConstraintAssertions.ThrowAsync(() => InsertAsync(otherNan));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Directed compiled schema: direction changes rebuild and survive rename/rewrite (#1631)")]
    public async Task ApplySchemaAsync_KeyOrderChange_ShouldRebuildAndRetainMetadata()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("directed-schema", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        SqlCompiledSchema Schema(CompiledSchemaIndexKeyOrder order) => new(SqlCompiledSchema.CurrentFormat, "app", true, [],
            [new("t", "Tests:Row", [new("a", Types.DatabaseType.Int32, true)], null,
                [new("ix", ["a"], KeyOrders: [order])], [])], []);
        await database.ApplySchemaAsync(Schema(CompiledSchemaIndexKeyOrder.AscendingNullsLast), CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "INSERT INTO t VALUES(1),(2),(NULL)");
        var desired = Schema(CompiledSchemaIndexKeyOrder.DescendingNullsLast);

        // Act
        var result = await database.ApplySchemaAsync(desired, CancellationToken.None);
        await database.VerifySchemaAsync(desired, CancellationToken.None);
        var repeat = await database.ApplySchemaAsync(desired, CancellationToken.None);
        await database.RewriteTableAsync("dbo", "t", cancellationToken: CancellationToken.None);

        // Assert
        result.OperationCount.ShouldBe(2);
        repeat.WasAlreadyApplied.ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        database.Catalog.GetIndexes(table.ObjectId).ShouldHaveSingleItem().KeyOrders.ShouldBe([SqlCatalogIndexKeyOrder.DescendingNullsLast]);
        (await Rows(session, "SELECT a FROM t WHERE a>0 ORDER BY a")).Select(row => row[0]).ShouldBe([1, 2]);
    }

    private static async Task Oracle(SqlDatabase database, SqlDatabaseSession session, string predicate)
    {
        var expected = (await Rows(session, "SELECT id FROM plain WHERE " + predicate + " ORDER BY id")).Select(row => row[0]).ToArray();
        var actual = (await Rows(session, "SELECT id FROM t WHERE " + predicate + " ORDER BY id")).Select(row => row[0]).ToArray();
        actual.ShouldBe(expected, predicate);
        // Probe the bound path directly as well: the runtime may cost a broad
        // range as a scan, which must not leave the range encoder unexercised.
        var request = SqlQueryRequest.FromSql("SELECT id FROM t WHERE " + predicate);
        var plan = new SqlPlanner(database.Catalog, null).Plan(request.Statement.SqlExpression).ShouldBeOfType<SqlSelectPlan>();
        var seek = plan.Access.ShouldBeOfType<SqlIndexSeekPath>();
        int[] ordinals = seek.Index.ColumnNames.Select(name => Enumerable.Range(0, plan.Table.Columns.Count).Single(i => plan.Table.Columns[i].Name == name)).ToArray();
        var range = SqlIndexKeyEncoding.BuildSeekRange(plan.Table, seek, ordinals, database.Catalog.DefaultCollation);
        database.IndexManager.TryGetIndex(plan.Table.ObjectId, seek.Index.Name, out var index).ShouldBeTrue();
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var evaluator = SqlExpressionEvaluator.ForExecution(null);
        var probed = new List<object?>();
        await using (var cursor = index.OpenCursor(transaction.Context, range))
        {
            while (await cursor.MoveNextAsync(CancellationToken.None))
            {
                var (page, slot) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);
                database.DataStorage.TryReadRecord(page, slot, plan.Table.ObjectId, out var record).ShouldBeTrue();
                var row = SqlRowCodec.Decode(record.Span, plan.Table, out _, out _, out _);
                if (evaluator.Matches(plan.Where, row)) { probed.Add(row[0]); }
            }
        }
        probed.OrderBy(value => value).ToArray().ShouldBe(expected, predicate);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private static async Task NullPrefix(SqlDatabase database, SqlDatabaseSession session, object?[] prefix, string predicate)
    {
        var expected = (await Rows(session, "SELECT id FROM plain WHERE " + predicate + " ORDER BY id")).Select(row => row[0]).ToArray();
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        database.Catalog.TryGetIndex(table.ObjectId, "ix", out var metadata).ShouldBeTrue();
        int[] ordinals = metadata.ColumnNames.Select(name => Enumerable.Range(0, table.Columns.Count).Single(i => table.Columns[i].Name == name)).ToArray();
        var range = SqlIndexKeyEncoding.BuildSeekRange(table, new SqlIndexSeekPath(metadata, prefix, null, null), ordinals, database.Catalog.DefaultCollation);
        database.IndexManager.TryGetIndex(table.ObjectId, "ix", out var index).ShouldBeTrue();
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var actual = new List<object?>();
        await using (var cursor = index.OpenCursor(transaction.Context, range))
        {
            while (await cursor.MoveNextAsync(CancellationToken.None))
            {
                var (page, slot) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);
                database.DataStorage.TryReadRecord(page, slot, table.ObjectId, out var record).ShouldBeTrue();
                actual.Add(SqlRowCodec.Decode(record.Span, table, out _, out _, out _)[0]);
            }
        }
        actual.OrderBy(value => value).ToArray().ShouldBe(expected, predicate);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private static async Task PhysicalOrder(SqlDatabase database, SqlDatabaseSession session, string order)
    {
        int[] expected = (await Rows(session, "SELECT id FROM plain ORDER BY " + order)).Select(row => (int)row[0]!).ToArray();
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        database.IndexManager.TryGetIndex(table.ObjectId, "ix", out var index).ShouldBeTrue();
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        foreach (bool reverse in new[] { false, true })
        {
            var actual = new List<int>();
            await using var cursor = index.OpenCursor(transaction.Context, IndexKeyRange.All, reverse);
            while (await cursor.MoveNextAsync(CancellationToken.None))
            {
                var (page, slot) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);
                database.DataStorage.TryReadRecord(page, slot, table.ObjectId, out var record).ShouldBeTrue();
                actual.Add((int)SqlRowCodec.Decode(record.Span, table, out _, out _, out _)[0]!);
            }
            actual.ShouldBe(reverse ? expected.Reverse() : expected);
        }
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private static string Suffix(int order) => order switch
    { 0 => " ASC NULLS LAST", 1 => " ASC NULLS FIRST", 2 => " DESC NULLS LAST", _ => " DESC NULLS FIRST" };
    private static Task Execute(SqlDatabaseSession session, string sql) => SqlPartialIndexTests.Execute(session, sql);
    private static Task<object?[][]> Rows(SqlDatabaseSession session, string sql) => SqlPartialIndexTests.Rows(session, sql);
}
