using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlRightJoinTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Derived-first groups execute their written join and nested association (#1525)")]
    [InlineData("((SELECT * FROM t0) d RIGHT JOIN t1 ON d.k = t1.k)", true, null, null, 2, 3)]
    [InlineData("(((SELECT * FROM t0)) AS d RIGHT OUTER JOIN t1 ON d.k = t1.k)", false, null, null, 2, 3)]
    [InlineData("(((SELECT * FROM t0) d RIGHT JOIN t1 ON d.k = t1.k))", false, null, null, 2, 3)]
    [InlineData("((SELECT * FROM t0) d INNER JOIN t1 ON TRUE)", false, 1, 10, 2, 3)]
    [InlineData("((SELECT * FROM t0) d LEFT JOIN t1 ON d.k = t1.k)", false, 1, 10, null, null)]
    [InlineData("((SELECT * FROM t0) d CROSS JOIN t1)", false, 1, 10, 2, 3)]
    public async Task Join_DerivedFirstGroup_ShouldPreserveRows(string from, bool emptyLeft, int? leftKey, int? leftValue, int? rightKey, int? rightValue)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("derived-first", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t0 (k INT, v0 INT); CREATE TABLE t1 (k INT, v1 INT); INSERT INTO t1 VALUES (2, 3);");
        if (!emptyLeft) { await ExecAsync(session, "INSERT INTO t0 VALUES (1, 10);"); }

        // Act
        var rows = await RowsAsync(session, "SELECT * FROM " + from);

        // Assert
        Check(rows, [[leftKey, leftValue, rightKey, rightValue]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Derived-first and nested grouped views round trip through creation and reopen (#1525)")]
    [InlineData("SELECT id FROM ((SELECT id FROM a) d RIGHT JOIN b USING (id))")]
    [InlineData("SELECT id FROM ((a RIGHT JOIN b USING (id)) q RIGHT JOIN c USING (id))")]
    [InlineData("SELECT id FROM (((SELECT id FROM a) d RIGHT JOIN b USING (id)) q RIGHT JOIN c USING (id))")]
    public async Task Open_DerivedFirstGroupView_ShouldRetainRowsAndMetadata(string body)
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "derived-first-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("files", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL); CREATE TABLE b (id INT NOT NULL); CREATE TABLE c (id INT NOT NULL); INSERT INTO b VALUES (2); INSERT INTO c VALUES (2);");
                Check(await RowsAsync(session, body), [[2]]);
                await ExecAsync(session, "CREATE VIEW v AS " + body);
                Check(await RowsAsync(session, "SELECT * FROM v;"), [[2]]);
            }

            // Act
            await using var reopened = SqlDatabaseEngine.Create("files", new() { RootPath = root });
            var next = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await next.CreateSessionAsync(CancellationToken.None);
            await using var result = (await reader.ExecuteAsync("SELECT * FROM v;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            var rows = await ReadAsync(result);

            // Assert
            Check(rows, [[2]]);
            result.Columns.Select(column => column.Name).ShouldBe(["id"]);
            result.Columns.Select(column => column.IsNullable).ShouldBe([false]);
            next.Catalog.Views.Single(view => view.Name == "v").Definition.ShouldNotContain("*", Case.Sensitive);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT: duplicate, NULL and empty bags equal reversed LEFT (#1525)")]
    [InlineData(false, "a.id = b.id")]
    [InlineData(true, "a.id = b.id")]
    [InlineData(false, "a.id < b.id")]
    [InlineData(true, "a.id = b.id AND a.v = 'keep'")]
    [InlineData(false, "NULL")]
    [InlineData(false, "FALSE")]
    public async Task Join_RightBags_ShouldEqualReversedLeft(bool indexed, string condition)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-bags", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT, v TEXT); CREATE TABLE b (id INT, v TEXT);");
        await ExecAsync(session, "INSERT INTO a VALUES (1, 'keep'), (1, 'other'), (NULL, 'null'), (4, 'left');");
        await ExecAsync(session, "INSERT INTO b VALUES (1, 'one'), (1, 'again'), (NULL, 'null'), (2, 'right');");
        if (indexed) { await ExecAsync(session, "CREATE INDEX ix_a ON a(id);"); }

        // Act / Assert
        for (int empty = 0; empty < 3; empty++)
        {
            var right = await RowsAsync(session, $"SELECT a.id, a.v, b.id, b.v FROM a RIGHT JOIN b ON {condition};");
            var left = await RowsAsync(session, $"SELECT a.id, a.v, b.id, b.v FROM b LEFT JOIN a ON {condition};");
            Bag(right).ShouldBe(Bag(left));
            await ExecAsync(session, empty == 0 ? "DELETE FROM a;" : "DELETE FROM b;");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT: wildcard slots, metadata, ON and WHERE preserve written inputs (#1525)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_RightNamespace_ShouldPreserveOrdinalsAndNullableLeft(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-slots", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (left_value TEXT NOT NULL, id INT NOT NULL); CREATE TABLE b (id INT NOT NULL, right_value TEXT NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES ('left', 1); INSERT INTO b VALUES (1, 'one'), (2, 'two');");
        if (indexed) { await ExecAsync(session, "CREATE INDEX ix_a ON a(id);"); }

        // Act
        await using var result = (await session.ExecuteAsync("SELECT * FROM a RIGHT OUTER JOIN b ON a.id = b.id ORDER BY 3;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);

        // Assert
        Check(rows, [["left", 1, 1, "one"], [null, null, 2, "two"]]);
        result.Columns.Select(column => column.Name).ShouldBe(["left_value", "id", "id", "right_value"]);
        result.Columns.Select(column => column.IsNullable).ShouldBe([true, true, false, false]);
        session.LastStatementMetrics!.JoinAccessPaths.ShouldBe(indexed ? ["join-seek:ix_a"] : ["join-hash"]);
        Check(await RowsAsync(session, "SELECT b.id FROM a RIGHT JOIN b ON a.id = b.id WHERE a.id IS NULL;"), [[2]]);
        Check(await RowsAsync(session, "SELECT a.id, b.id FROM a RIGHT JOIN b ON a.id = b.id AND b.id = 2 ORDER BY b.id;"), [[null, 1], [null, 2]]);
        Check(await RowsAsync(session, "SELECT a.id, b.id FROM a RIGHT JOIN b ON a.id = b.id WHERE b.id = 2;"), [[null, 2]]);
        Check(await RowsAsync(session, "SELECT COUNT(*), COUNT(a.id) FROM a RIGHT JOIN b ON a.id = b.id;"), [[2L, 1L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT USING: merged right spelling, common type and wildcard order survive (#1525)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Join_RightUsing_ShouldTakeCoercedRightValue(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-using", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (extra TEXT NOT NULL, id DECIMAL NOT NULL, name VARCHAR(8) COLLATE case_insensitive NOT NULL); CREATE TABLE b (id INT NOT NULL, name VARCHAR(12) COLLATE case_insensitive NOT NULL, tail TEXT NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES ('L', 1, 'Ada'); INSERT INTO b VALUES (1, 'ADA', 'R'), (2, 'Grace', 'unmatched');");
        if (indexed) { await ExecAsync(session, "CREATE INDEX ix_a ON a(id, name);"); }

        // Act
        await using var result = (await session.ExecuteAsync("SELECT *, a.id AS old_id, b.id AS new_id FROM a RIGHT JOIN b USING (name, id) ORDER BY id;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);

        // Assert
        Check(rows, [["ADA", 1m, "L", "R", 1m, 1], ["Grace", 2m, null, "unmatched", null, 2]]);
        result.Columns.Select(column => column.Name).ShouldBe(["name", "id", "extra", "tail", "old_id", "new_id"]);
        result.Columns.Select(column => column.IsNullable).ShouldBe([false, false, true, false, true, false]);
        result.Columns[0].MaxLength.ShouldBeNull();
        result.Columns[1].Type.ShouldBe(DatabaseType.Decimal);
        Check(await RowsAsync(session, "SELECT name, a.name, b.name FROM a RIGHT JOIN b USING (id, name) ORDER BY id;"),
            [["ADA", "Ada", "ADA"], ["Grace", null, "Grace"]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT USING output keeps the preserved column's collation (#1525)")]
    [InlineData("name")]
    [InlineData("*")]
    public async Task Join_RightUsingCollation_ShouldKeepRightComparisonIdentity(string projection)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-collation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (name TEXT COLLATE case_insensitive); CREATE TABLE b (name TEXT); INSERT INTO b VALUES ('a'), ('A');");

        // Act
        var rows = await RowsAsync(session, $"SELECT DISTINCT {projection} FROM a RIGHT JOIN b USING(name) ORDER BY name;");

        // Assert
        Check(rows, [["A"], ["a"]]);
        Check(await RowsAsync(session, "SELECT COUNT(*) FROM a RIGHT JOIN b USING(name) WHERE name = 'A';"), [[1L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT chains and groups preserve SQL association (#1525)")]
    [InlineData("a LEFT JOIN b ON a.id = b.id RIGHT JOIN c ON b.id = c.id", false)]
    [InlineData("(a LEFT JOIN b ON a.id = b.id) RIGHT JOIN c ON b.id = c.id", false)]
    [InlineData("a LEFT JOIN (b RIGHT JOIN c ON b.id = c.id) ON a.id = b.id", true)]
    public async Task Join_MixedAssociation_ShouldPreserveSubtree(string from, bool preservesA)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-chains", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL); CREATE TABLE b (id INT NOT NULL); CREATE TABLE c (id INT NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES (1), (2); INSERT INTO b VALUES (1); INSERT INTO c VALUES (1), (3);");
        await ExecAsync(session, "CREATE INDEX ix_a ON a(id);");

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT a.id, b.id, c.id FROM {from} ORDER BY c.id NULLS LAST, a.id;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);

        // Assert
        Check(rows, preservesA ? [[1, 1, 1], [2, null, null]] : [[1, 1, 1], [null, null, 3]]);
        result.Columns.Select(column => column.IsNullable).ShouldBe(preservesA ? [false, true, true] : [true, true, false]);
        Check(await RowsAsync(session, "SELECT c.id FROM a empty_side, b RIGHT JOIN c ON b.id = c.id WHERE empty_side.id = 2 ORDER BY c.id;"), [[1], [3]]);
        await ExecAsync(session, "DELETE FROM a;");
        (await RowsAsync(session, "SELECT c.id FROM a, b RIGHT JOIN c ON b.id = c.id;")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT: derived, CTE, view and aliased group consumers retain metadata (#1525)")]
    [InlineData("SELECT aid, id FROM q ORDER BY id", "WITH q AS (SELECT a.id AS aid, b.id FROM a RIGHT JOIN b ON a.id = b.id) ")]
    [InlineData("SELECT aid, id FROM (SELECT a.id AS aid, b.id FROM a RIGHT JOIN b ON a.id = b.id) q ORDER BY id", "")]
    [InlineData("SELECT aid, id FROM v ORDER BY id", "")]
    [InlineData("SELECT q.aid, q.id FROM (a RIGHT JOIN b ON a.id = b.id) AS q(aid, id) ORDER BY q.id", "")]
    public async Task Join_RelationConsumers_ShouldRetainValuesAndNullability(string query, string prefix)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-consumers", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL); CREATE TABLE b (id INT NOT NULL); INSERT INTO a VALUES (1); INSERT INTO b VALUES (1), (2);");
        await ExecAsync(session, "CREATE VIEW v AS SELECT a.id AS aid, b.id FROM a RIGHT JOIN b ON a.id = b.id;");

        // Act
        await using var result = (await session.ExecuteAsync(prefix + query, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);

        // Assert
        Check(rows, [[1, 1], [null, 2]]);
        result.Columns.Select(column => column.IsNullable).ShouldBe([true, false]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - RIGHT: ON subquery snapshots precede later USING merges (#1525)")]
    public async Task Join_CorrelatedOn_ShouldKeepItsWrittenNamespace()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-correlations", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL); CREATE TABLE b (id INT NOT NULL); CREATE TABLE c (k INT NOT NULL); CREATE TABLE d (id INT NOT NULL);");
        await ExecAsync(session, "INSERT INTO a VALUES (1); INSERT INTO b VALUES (1), (2); INSERT INTO c VALUES (2); INSERT INTO d VALUES (2), (3);");

        // Act / Assert
        Check(await RowsAsync(session, "SELECT id, a.id, b.id, c.k, d.id FROM (a RIGHT JOIN b USING (id)) LEFT JOIN c ON EXISTS (SELECT 1 WHERE id = c.k) RIGHT JOIN d USING (id) ORDER BY id;"),
            [[2, null, 2, 2, 2], [3, null, null, null, 3]]);
        Check(await RowsAsync(session, "SELECT a.id, b.id FROM a RIGHT JOIN b ON EXISTS (SELECT 1 WHERE a.id = b.id) ORDER BY b.id;"), [[1, 1], [null, 2]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT: binding errors precede reading even for empty relations (#1525)")]
    [InlineData("SELECT id FROM a RIGHT JOIN b ON a.id = b.id", "CS01015")]
    [InlineData("SELECT a.id FROM a x RIGHT JOIN b USING(id)", "CS01015")]
    [InlineData("SELECT * FROM a RIGHT JOIN b USING(missing)", "CS01015")]
    [InlineData("SELECT * FROM a RIGHT JOIN b USING(id,id)", "CS01015")]
    [InlineData("SELECT * FROM a RIGHT JOIN (SELECT TRUE AS id) b USING(id)", "CS01015")]
    [InlineData("SELECT * FROM a RIGHT JOIN b ON c.id = b.id JOIN a c ON TRUE", "CS01015")]
    [InlineData("SELECT * FROM a, b RIGHT JOIN a c ON a.id = c.id", "CS01015")]
    [InlineData("SELECT * FROM a RIGHT JOIN b a USING(id)", "CS01019")]
    [InlineData("UPDATE a SET id = b.id FROM b RIGHT JOIN a c ON b.id = c.id", "CS05069")]
    [InlineData("DELETE FROM a USING b RIGHT JOIN a c ON b.id = c.id", "CS05069")]
    public async Task Join_InvalidNamespace_ShouldRefuseBeforeReads(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-errors", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT); CREATE TABLE b (id INT);");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - RIGHT: derived joined DML sources retain the target identity (#1525)")]
    public async Task Join_DerivedDmlSource_ShouldKeepTargetPinned()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-dml", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t (id INT, v INT); CREATE TABLE a (id INT); CREATE TABLE b (id INT); INSERT INTO t VALUES (1, 0), (2, 0); INSERT INTO b VALUES (2);");

        // Act / Assert
        await ExecAsync(session, "UPDATE t SET v = q.id FROM (SELECT b.id FROM a RIGHT JOIN b USING (id)) q WHERE t.id = q.id;");
        Check(await RowsAsync(session, "SELECT * FROM t ORDER BY id;"), [[1, 0], [2, 2]]);
        await ExecAsync(session, "DELETE FROM t USING (SELECT b.id FROM a RIGHT JOIN b USING (id)) q WHERE t.id = q.id;");
        Check(await RowsAsync(session, "SELECT * FROM t;"), [[1, 0]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT EXPLAIN identifies normalization and its physical path (#1525)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explain_RightJoin_ShouldIdentifyNormalization(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-explain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL); CREATE TABLE b (id INT NOT NULL); INSERT INTO a VALUES (1); INSERT INTO b VALUES (1), (2);");
        if (indexed) { await ExecAsync(session, "CREATE INDEX ix_a ON a(id);"); }

        // Act
        var plan = await RowsAsync(session, "EXPLAIN ANALYZE SELECT * FROM a RIGHT JOIN b ON a.id = b.id;");

        // Assert
        var step = plan.Single(row => Equals(row[2], "JoinStep"));
        step[5]!.ToString()!.ShouldContain("normalized RIGHT to LEFT", Case.Sensitive);
        step[5]!.ToString()!.ShouldContain(indexed ? "IndexNestedLoop" : "HashJoin", Case.Sensitive);
        Convert.ToInt64(step[7]).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - RIGHT: system inputs share the catalog image and USING domains retain identity (#1525)")]
    public async Task Join_SystemAndDomainInputs_ShouldPreserveMetadata()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-system-domain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE DOMAIN positive AS INT CHECK(VALUE > 0); CREATE TABLE a (id positive NOT NULL); CREATE TABLE b (id positive NOT NULL); INSERT INTO b VALUES (2);");

        // Act / Assert
        Check(await RowsAsync(session, "SELECT a.id, t.TABLE_NAME FROM a RIGHT JOIN INFORMATION_SCHEMA.TABLES t ON FALSE WHERE t.TABLE_NAME = 'a';"), [[null, "a"]]);
        session.LastStatementMetrics!.CatalogSnapshotCaptures.ShouldBe(1);
        Check(await RowsAsync(session, "SELECT id, a.id, b.id FROM a RIGHT JOIN b USING (id);"), [[2, null, 2]]);
        await ExecAsync(session, "CREATE VIEW domain_view AS SELECT id FROM a RIGHT JOIN b USING (id);");
        database.Catalog.Views.Single(view => view.Name == "domain_view").Columns.Single().DomainName.ShouldBe("positive");
        Check(await RowsAsync(session, "SELECT id FROM domain_view;"), [[2]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT: hash refusal releases its reservation and leaves the session usable (#1525)")]
    [InlineData("")]
    [InlineData("CROSS JOIN c")]
    [InlineData("JOIN c ON a.id = c.marker")]
    public async Task Join_HashBudgetFailure_ShouldReleaseMemory(string innerJoin)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-budget", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT); CREATE TABLE b (id INT); INSERT INTO b VALUES (1);");
        await ExecAsync(session, "INSERT INTO a VALUES " + string.Join(',', Enumerable.Range(1, 100).Select(id => $"({id})")));
        await ExecAsync(session, "CREATE TABLE c (marker INT);");
        await ExecAsync(session, "INSERT INTO c VALUES " + string.Join(',', Enumerable.Repeat("(1)", 20)));

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"SELECT * FROM a {innerJoin} RIGHT JOIN b ON a.id = b.id;", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(1024);
        session.LastStatementMetrics.JoinCandidatePairs.ShouldBeLessThan(100);
        if (innerJoin.Length == 0) { session.LastStatementMetrics.RecordsExamined.ShouldBeLessThan(100); }
        Check(await RowsAsync(session, "SELECT COUNT(*) FROM a;"), [[100L]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - RIGHT: long chains use an iterative physical stack (#1525)")]
    public async Task Join_LongNormalizedChain_ShouldAvoidRecursiveExecution()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-long", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t (id INT); INSERT INTO t VALUES (1);");
        string from = "t a0 " + string.Join(' ', Enumerable.Range(1, 1024).Select(id => $"RIGHT JOIN t a{id} ON FALSE"));

        // Act
        var rows = await RowsAsync(session, "SELECT a0.id, a1024.id FROM " + from);

        // Assert
        Check(rows, [[null, 1]]);
        session.LastStatementMetrics!.JoinAccessPaths.Count.ShouldBe(1024);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - RIGHT grouped views freeze merged outputs and dependencies (#1525)")]
    [InlineData("SELECT * FROM (a RIGHT JOIN b USING(id))")]
    [InlineData("SELECT * FROM (a RIGHT JOIN b USING(id)) AS q")]
    public async Task Join_GroupedView_ShouldFreezeUsingOutput(string body)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-group-view", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL); CREATE TABLE b (id INT NOT NULL); INSERT INTO b VALUES (2);");
        await ExecAsync(session, "CREATE VIEW v AS " + body);

        // Act
        await ExecAsync(session, "ALTER TABLE a ADD COLUMN extra INT; ALTER TABLE b ADD COLUMN tail INT;");
        var rows = await RowsAsync(session, "SELECT * FROM v;");

        // Assert
        Check(rows, [[2]]);
        database.Catalog.Views.Single(view => view.Name == "v").Definition.ShouldNotContain("*", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - RIGHT views retain namespaces and nullability after reopen (#1525)")]
    public async Task Open_RightJoinView_ShouldRetainFrozenMetadata()
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "right-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("files", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL, left_tag TEXT NOT NULL); CREATE TABLE b (id INT NOT NULL); INSERT INTO b VALUES (2);");
                await ExecAsync(session, "CREATE VIEW v AS SELECT * FROM (a RIGHT JOIN b USING(id)); CREATE VIEW w AS SELECT * FROM (a RIGHT JOIN b USING(id)) AS q;");
                await ExecAsync(session, "ALTER TABLE a ADD COLUMN extra INT; ALTER TABLE b ADD COLUMN tail INT;");
            }

            // Act / Assert
            await using var reopened = SqlDatabaseEngine.Create("files", new() { RootPath = root });
            var next = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await next.CreateSessionAsync(CancellationToken.None);
            foreach (string view in new[] { "v", "w" })
            {
                await using var result = (await reader.ExecuteAsync("SELECT * FROM " + view, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
                Check(await ReadAsync(result), [[2, null]]);
                result.Columns.Select(column => column.IsNullable).ShouldBe([false, true]);
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static async Task ExecAsync(SqlDatabaseSession session, string sql)
    {
        foreach (string statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
        }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadAsync(result);
    }

    private static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static string[] Bag(IEnumerable<object?[]> rows) => rows.Select(row => string.Join("|", row.Select(value => value?.ToString() ?? "<NULL>"))).Order(StringComparer.Ordinal).ToArray();

    private static void Check(IReadOnlyList<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int i = 0; i < actual.Count; i++) { actual[i].ShouldBe(expected[i]); }
    }
}
