using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlPartialIndexImplicationTests
{
    private readonly ITestOutputHelper _output;

    public SqlPartialIndexImplicationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial implication: every generated proof agrees with a three-valued evaluation oracle (#1630)")]
    public async Task Prove_GeneratedPredicates_ShouldNeverAdmitCounterexamples()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-proof-oracle", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(x INT,b BOOLEAN,s TEXT)");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var scope = new SqlExpressionEvaluator(table.Columns, null, foldConstants: false);
        var predicates = new List<string> { "x IS NULL", "x IS NOT NULL", "b", "b=TRUE", "b=FALSE", "NOT b", "x>0 OR b", "s='a'", "s>'A'" };
        foreach (string op in new[] { "=", "<>", ">", ">=", "<", "<=" })
        {
            foreach (int value in Enumerable.Range(-2, 5))
            { predicates.Add($"x {op} {value}"); predicates.Add($"{value} {op} x"); }
        }
        predicates.AddRange(new[] { "x>0 AND b", "b=TRUE AND x>=1", "x>1 AND x<3", "(x>0 AND b) AND x<3",
            "CAST(x AS BIGINT)>0", "ABS(x)>0", "COALESCE(x,0)>0", "CASE WHEN b THEN x ELSE 0 END>0",
            "x BETWEEN 0 AND 2", "x IN(1,2)", "s LIKE 'a%'", "s ILIKE 'A%'", "x IS DISTINCT FROM 1", "NULLIF(x,1)>0",
            "s>'A' COLLATE case_insensitive" });
        var bound = predicates.Select(predicate => scope.BindPredicate(ParsePredicate(predicate))).ToArray();
        for (int i = 0; i < predicates.Count; i++)
        {
            if (predicates[i] == "NOT b" || predicates[i].Contains(" OR ", StringComparison.Ordinal)) { continue; }
            SqlPredicateImplication.Prove(bound[i], [scope.BindPredicate(ParsePredicate(predicates[i]))]).ShouldNotBeNull(predicates[i]);
        }
        var rows = (from x in new object?[] { null, -3, -2, -1, 0, 1, 2, 3 }
                    from b in new object?[] { null, false, true }
                    from s in new object?[] { null, "", "A", "a", "b" }
                    select new[] { x, b, s }).ToArray();
        var truth = bound.Select(expression => rows.Select(row => scope.Evaluate(expression, row) is true).ToArray()).ToArray();
        int proven = 0;

        // Act / Assert: unproven implications may be valid; a proved implication must be valid.
        for (int q = 0; q < bound.Length; q++)
        {
            for (int p = 0; p < bound.Length; p++)
            {
                if (SqlPredicateImplication.Prove(bound[p], [bound[q]]) is null) { continue; }
                proven++;
                for (int row = 0; row < rows.Length; row++)
                { (truth[q][row] && !truth[p][row]).ShouldBeFalse($"{predicates[q]} => {predicates[p]} at [{string.Join(',', rows[row])}]"); }
            }
        }
        proven.ShouldBeGreaterThan(200);
        _output.WriteLine($"Oracle: {bound.Length * bound.Length} predicate pairs, {rows.Length} three-valued rows, {proven} proofs.");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial access: only proven local restrictions select a subset seek (#1630)")]
    [InlineData("x>10 AND b", true)]
    [InlineData("10<x AND b=TRUE", true)]
    [InlineData("x=7 AND b=TRUE", true)]
    [InlineData("x>=5 AND b", false)]
    [InlineData("x>10", false)]
    [InlineData("x>10 OR b", false)]
    [InlineData("NOT (x<=10) AND b", false)]
    [InlineData("x>10 AND NOT NOT b", false)]
    public async Task ExecuteAsync_ComparisonRules_ShouldSelectOnlyProvenIndexes(string predicate, bool selected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-access-rules", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(x INT,b BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(NULL,TRUE),(4,TRUE),(5,TRUE),(7,TRUE),(11,TRUE),(12,FALSE),(13,NULL)");
        string query = $"SELECT x,b FROM t AS a WHERE {predicate} ORDER BY x,b";
        var expected = await SqlPartialIndexTests.Rows(session, query);
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(x) WHERE x>5 AND b");

        // Act
        var plan = await SqlPartialIndexTests.Rows(session, "EXPLAIN " + query);
        var actual = await SqlPartialIndexTests.Rows(session, query);

        // Assert
        Canonical(actual).ShouldBe(Canonical(expected));
        string text = Canonical(plan);
        text.Contains("partial_predicate=", StringComparison.Ordinal).ShouldBe(selected, text);
        if (selected) { text.ShouldContain("implied_by=", Case.Sensitive); text.ShouldContain("ix", Case.Sensitive); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial joins: generated inner, outer, grouped and correlated results agree with scans (#1630)")]
    [InlineData("SELECT t.id,u.id FROM u JOIN t ON t.k=u.k AND t.b WHERE u.k>=0 ORDER BY t.id,u.id", true)]
    [InlineData("SELECT t.id,u.id FROM u JOIN t ON t.k=u.k WHERE t.b ORDER BY t.id,u.id", true)]
    [InlineData("SELECT t.id,u.id FROM u LEFT JOIN t ON t.k=u.k AND t.b ORDER BY t.id,u.id", false)]
    [InlineData("SELECT t.id,u.id FROM u LEFT JOIN t ON t.k=u.k WHERE t.b ORDER BY t.id,u.id", false)]
    [InlineData("SELECT t.id,u.id FROM t LEFT JOIN u ON t.k=u.k WHERE t.b ORDER BY t.id,u.id", true)]
    [InlineData("SELECT t.id,u.id FROM t RIGHT JOIN u ON t.k=u.k AND t.b ORDER BY t.id,u.id", false)]
    [InlineData("SELECT t.id,u.id FROM t FULL JOIN u ON t.k=u.k AND t.b ORDER BY t.id,u.id", false)]
    [InlineData("SELECT t.id,u.id FROM (u JOIN t ON t.k=u.k AND t.b) ORDER BY t.id,u.id", true)]
    [InlineData("SELECT t.id,u.id FROM u JOIN (t JOIN u v ON t.k=v.k) ON t.k=u.k AND t.b ORDER BY t.id,u.id", true)]
    [InlineData("SELECT id FROM u WHERE EXISTS(SELECT id FROM t WHERE t.k=u.k AND t.b) ORDER BY id", true)]
    [InlineData("SELECT id FROM u WHERE EXISTS(SELECT id FROM t WHERE t.k=u.k) ORDER BY id", false)]
    public async Task ExecuteAsync_JoinScopes_ShouldMatchWithoutPartialIndexes(string query, bool selected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-join-oracle", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(id INT,k INT,b BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE u(id INT,k INT)");
        var random = new Random(163002);
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(0, 48).Select(id => $"({id},{random.Next(8)},{(id % 3 == 0 ? "NULL" : id % 2 == 0 ? "TRUE" : "FALSE")})")));
        await SqlPartialIndexTests.Execute(session, "INSERT INTO u VALUES " + string.Join(',', Enumerable.Range(0, 16).Select(id => $"({id},{id % 9})")));
        var expected = await SqlPartialIndexTests.Rows(session, query);
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(k) WHERE b");

        // Act
        var plan = await SqlPartialIndexTests.Rows(session, "EXPLAIN " + query);
        var actual = await SqlPartialIndexTests.Rows(session, query);

        // Assert
        Canonical(actual).ShouldBe(Canonical(expected));
        Canonical(plan).Contains("partial_predicate=", StringComparison.Ordinal).ShouldBe(selected, Canonical(plan));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial proof origins: an unrelated INNER ON FALSE cannot restrict a preserved outer input (#1630)")]
    public async Task ExecuteAsync_ConstantOnInOtherSubtree_ShouldPreserveOuterRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-on-origin", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "a", "b", "c" })
        { await SqlPartialIndexTests.Execute(session, $"CREATE TABLE {table}(id INT)"); }
        await SqlPartialIndexTests.Execute(session, "INSERT INTO c VALUES(1),(2)");
        const string query = "SELECT c.id FROM (a JOIN b ON FALSE) RIGHT JOIN c ON TRUE ORDER BY c.id";
        var expected = await SqlPartialIndexTests.Rows(session, query);
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX dead ON c(id) WHERE FALSE");

        // Act
        var actual = await SqlPartialIndexTests.Rows(session, query);
        var plan = await SqlPartialIndexTests.Rows(session, "EXPLAIN " + query);

        // Assert
        Canonical(expected).ShouldBe("1\n2");
        Canonical(actual).ShouldBe(Canonical(expected));
        Canonical(plan).ShouldNotContain("partial_predicate=", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial access: a selective range reduces deterministic row visits (#1630)")]
    public async Task ExecuteAsync_SelectiveRange_ShouldReduceRowsExamined()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-work", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(x INT,b BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(0, 512).Select(x => $"({x},{(x % 8 == 0 ? "TRUE" : "FALSE")})")));
        const string query = "SELECT x FROM t WHERE x>=480 AND b ORDER BY x";
        var expected = await SqlPartialIndexTests.Rows(session, query);
        long before = session.LastStatementMetrics!.RecordsExamined;
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(x) WHERE b AND x IS NOT NULL");

        // Act
        var actual = await SqlPartialIndexTests.Rows(session, query);
        long after = session.LastStatementMetrics!.RecordsExamined;

        // Assert
        Canonical(actual).ShouldBe(Canonical(expected));
        before.ShouldBe(512);
        after.ShouldBe(4);
        _output.WriteLine($"Partial range: {before} -> {after} heap rows examined.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial null proof: strict inequality proves non-null membership (#1630)")]
    public async Task ExecuteAsync_StrictInequality_ShouldProveNonNull()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-strict-null", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(x INT)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(NULL),(0),(1),(2)");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(x) WHERE x IS NOT NULL");

        // Act
        var rows = await SqlPartialIndexTests.Rows(session, "SELECT x FROM t WHERE x<>1 ORDER BY x");

        // Assert
        Canonical(rows).ShouldBe("0\n2");
        session.LastStatementMetrics!.AccessPath.ShouldBe("seek:ix");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial arbiters: inference uses the target predicate and membership for every proposal (#1630)")]
    public async Task ExecuteAsync_ConflictTargetWhere_ShouldInferOnlyProvenArbiters()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-inference", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(id INT,k INT,b BOOLEAN,n INT)");
        await SqlPartialIndexTests.Execute(session, "CREATE UNIQUE INDEX uq ON t(k) WHERE b AND n>0");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(1,7,TRUE,1),(2,7,FALSE,1)");

        var plan = await SqlPartialIndexTests.Rows(session, "EXPLAIN INSERT INTO t VALUES(3,7,TRUE,1) ON CONFLICT(k) WHERE n>=1 AND b=TRUE DO NOTHING");
        Canonical(plan).ShouldContain("ConflictArbiter|dbo.t|uq|partial_predicate=", Case.Sensitive);
        Canonical(plan).ShouldContain("implied_by=", Case.Sensitive);

        // Act / Assert
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(3,7,TRUE,1) ON CONFLICT(k) DO NOTHING", "CS03035");
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(3,7,TRUE,1) ON CONFLICT(k) WHERE b DO NOTHING", "CS03035");
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(3,7,TRUE,1) ON CONFLICT ON CONSTRAINT uq DO NOTHING", "CS03035");
        (await session.ExecuteAsync("INSERT INTO t VALUES(3,7,TRUE,1) ON CONFLICT(k) WHERE n>=1 AND b=TRUE DO NOTHING", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("INSERT INTO t VALUES(3,7,FALSE,1) ON CONFLICT(k) WHERE n>=1 AND b DO NOTHING", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await session.ExecuteAsync("INSERT INTO t VALUES(4,7,TRUE,2) ON CONFLICT(k) WHERE n>1 AND b DO UPDATE SET n=excluded.n WHERE t.n<excluded.n", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        (await SqlPartialIndexTests.Rows(session, "SELECT n FROM t WHERE b")).Single().Single().ShouldBe(2);
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(3,8,TRUE,1) ON CONFLICT(k) WHERE random()>0 DO NOTHING", "CS03035");
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(3,8,TRUE,1) ON CONFLICT(k) WHERE EXISTS(SELECT 1) DO NOTHING", "CS05092");
        (await session.ExecuteAsync("INSERT INTO t VALUES(8,7,TRUE,3),(9,7,TRUE,4) ON CONFLICT(k) WHERE n>0 AND b DO UPDATE SET b=FALSE", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        (await SqlPartialIndexTests.Rows(session, "SELECT id FROM t WHERE b ORDER BY id")).Single().Single().ShouldBe(9);
        (await session.ExecuteAsync("INSERT INTO t VALUES(10,NULL,TRUE,1),(11,NULL,TRUE,1) ON CONFLICT(k) WHERE n>0 AND b DO UPDATE SET n=2", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial DML: joined targets and MERGE sources agree with scan execution (#1630)")]
    [InlineData("UPDATE t SET n=1 FROM u WHERE t.k=u.k AND t.b", true)]
    [InlineData("DELETE FROM t USING u WHERE t.k=u.k AND t.b", true)]
    [InlineData("MERGE INTO t USING u ON t.k=u.k AND u.b WHEN MATCHED THEN UPDATE SET n=1", true)]
    [InlineData("MERGE INTO t USING u ON t.k=u.k AND u.b WHEN MATCHED THEN UPDATE SET n=1 WHEN NOT MATCHED THEN INSERT VALUES(u.id,u.k,u.b,2)", false)]
    public async Task ExecuteAsync_JoinedDml_ShouldMatchScans(string sql, bool selected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-dml-oracle", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "u" })
        {
            await SqlPartialIndexTests.Execute(session, $"CREATE TABLE {table}(id INT,k INT,b BOOLEAN,n INT)");
            await SqlPartialIndexTests.Execute(session, $"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(0, 32).Select(id => $"({id},{id},{(id % 3 == 0 ? "NULL" : id % 2 == 0 ? "TRUE" : "FALSE")},0)")));
        }
        await SqlPartialIndexTests.Execute(session, "BEGIN");
        await SqlPartialIndexTests.Execute(session, sql);
        var expected = await SqlPartialIndexTests.Rows(session, "SELECT * FROM t ORDER BY id");
        await SqlPartialIndexTests.Execute(session, "ROLLBACK");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix_t ON t(k) WHERE b");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix_u ON u(k) WHERE b");

        // Act
        var plan = await SqlPartialIndexTests.Rows(session, "EXPLAIN " + sql);
        await SqlPartialIndexTests.Execute(session, sql);
        var actual = await SqlPartialIndexTests.Rows(session, "SELECT * FROM t ORDER BY id");

        // Assert
        Canonical(actual).ShouldBe(Canonical(expected));
        Canonical(plan).Contains("partial_predicate=", StringComparison.Ordinal).ShouldBe(selected, Canonical(plan));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial referential lookup: the check itself must prove completeness (#1630)")]
    [InlineData("pid IS NOT NULL", true)]
    [InlineData("pid IS NOT NULL AND b", false)]
    public async Task ExecuteAsync_ReferentialLookup_ShouldUseOnlyItsOwnPredicate(string predicate, bool selected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-fk-proof", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE p(id INT PRIMARY KEY)");
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE c(pid INT REFERENCES p(id) ON DELETE CASCADE,b BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX child_pid ON c(pid) WHERE " + predicate);
        await SqlPartialIndexTests.Execute(session, "INSERT INTO p VALUES(1),(2)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO c VALUES(1,TRUE),(1,FALSE),(NULL,TRUE),(2,TRUE)");

        // Act
        await SqlPartialIndexTests.Execute(session, "DELETE FROM p WHERE id=1");
        string access = session.LastStatementMetrics!.AccessPath!;
        var rows = await SqlPartialIndexTests.Rows(session, "SELECT pid,b FROM c ORDER BY pid,b");

        // Assert
        access.ShouldBe(selected ? "constraint-seek:child_pid" : "constraint-scan");
        Canonical(rows).ShouldBe("NULL|True\n2|True");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial result oracle: generated comparisons and join forms agree with index-free execution (#1630)")]
    public async Task ExecuteAsync_GeneratedQueries_ShouldMatchWithoutIndexes()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-query-oracle", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(id INT,x INT,b BOOLEAN,s TEXT)");
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE u(x INT)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(0, 64).Select(id =>
            $"({id},{(id % 7 == 0 ? "NULL" : (id % 13 - 6).ToString())},{(id % 3 == 0 ? "NULL" : id % 2 == 0 ? "TRUE" : "FALSE")},'{(id % 2 == 0 ? "a" : "A")}')")));
        await SqlPartialIndexTests.Execute(session, "INSERT INTO u VALUES(NULL),(-5),(0),(3),(6)");
        var queries = new List<string>();
        foreach (string op in new[] { "=", ">", ">=", "<", "<=" })
        {
            foreach (int value in new[] { -5, 0, 3, 6 })
            {
                string restriction = $"t.x{op}{value} AND t.b=TRUE";
                queries.Add($"SELECT t.id FROM t WHERE {restriction} ORDER BY t.id");
                queries.Add($"SELECT t.id,u.x FROM u JOIN t ON t.x=u.x AND {restriction} ORDER BY t.id,u.x");
                queries.Add($"SELECT t.id,u.x FROM u LEFT JOIN t ON t.x=u.x AND {restriction} ORDER BY t.id,u.x");
                queries.Add($"SELECT t.id,u.x FROM t RIGHT JOIN u ON t.x=u.x WHERE {restriction} ORDER BY t.id,u.x");
            }
        }
        queries.Add("SELECT id FROM t WHERE s='A' COLLATE case_insensitive AND x=3 ORDER BY id");
        queries.Add("SELECT id FROM t WHERE x>3 OR b ORDER BY id");
        var expected = new List<string>();
        foreach (string query in queries) { expected.Add(Canonical(await SqlPartialIndexTests.Rows(session, query))); }
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(x) WHERE x>0 AND b");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix_text ON t(x) WHERE s='A' COLLATE binary");

        // Act / Assert
        for (int i = 0; i < queries.Count; i++)
        { Canonical(await SqlPartialIndexTests.Rows(session, queries[i])).ShouldBe(expected[i], queries[i]); }
        _output.WriteLine($"Generated result oracle: {queries.Count} queries across scalar, inner, LEFT and RIGHT access paths.");
    }

    private static SqlExpression ParsePredicate(string predicate)
        => ((SqlSelectExpression)((SqlQueryStatement)new SqlQueryParser().Parse("SELECT * FROM t WHERE " + predicate)).SqlExpression).Where!;

    private static string Canonical(IEnumerable<object?[]> rows)
        => string.Join("\n", rows.Select(row => string.Join('|', row.Select(value => value?.ToString() ?? "NULL"))));
}
