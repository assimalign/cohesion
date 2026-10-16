using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>CHAR comparison regressions across table, SQL parameter and domain VALUE binding scopes.</summary>
public sealed class SqlCharacterBindingTests
{
    // PostgreSQL functions.c:491-505 binds SQL arguments as Param, typecmds.c:3603-3618
    // binds VALUE as CoerceToDomainValue. Neither is a Var in parse_expr.c:1163-1190.
    // parse_coerce.c:1397-1456 and pg_cast.dat:313-325 keep the first known string type.
    // varchar.c:746-776 ignores CHAR padding; varlena.c:1429-1480 retains TEXT spaces.
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SQL function parameters stay in the IN common-type group")]
    [InlineData("p", false)]
    [InlineData("CAST(p AS CHAR(3))", false)]
    [InlineData("char_echo(p)", false)]
    [InlineData("p COLLATE binary", false)]
    [InlineData("CASE WHEN TRUE THEN p ELSE NULL END", false)]
    [InlineData("COALESCE(p, NULL)", false)]
    [InlineData("p", true)]
    [InlineData("CAST(p AS CHAR(3))", true)]
    [InlineData("char_echo(p)", true)]
    [InlineData("p COLLATE binary", true)]
    [InlineData("CASE WHEN TRUE THEN p ELSE NULL END", true)]
    [InlineData("COALESCE(p, NULL)", true)]
    public async Task In_FunctionParameter_ShouldUseCommonType(string candidate, bool characterFirst)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-binding", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT, c CHAR(3))");
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, NULL)");
        await session.ExecuteAsync("CREATE FUNCTION char_echo(p CHAR(3)) RETURNS CHAR(3) IMMUTABLE RETURN p");
        string candidates = characterFirst ? $"{candidate}, CAST('b' AS TEXT)" : $"CAST('b' AS TEXT), {candidate}";
        await session.ExecuteAsync($"CREATE FUNCTION in_arg(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN 'a ' IN ({candidates})");
        await session.ExecuteAsync($"CREATE FUNCTION not_in_arg(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN 'a ' NOT IN ({candidates})");

        // Act
        var rows = await RowsAsync(session, "SELECT in_arg(c), not_in_arg(c), in_arg('a') FROM t ORDER BY id");

        // Assert: dynamic frame values, constant-call folding and NULL/NOT IN all use the same binding.
        // Strict NULL handling: PostgreSQL execExprInterp.c:4054-4074,4165-4185.
        rows.ShouldBe([[characterFirst, !characterFirst, characterFirst], [false, true, characterFirst], [null, null, characterFirst]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Domain VALUE cannot bypass the reviewer's CHAR IN constraint")]
    public async Task In_DomainValue_ShouldRejectReviewWrite()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-domain-in", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION text_b() RETURNS TEXT IMMUTABLE RETURN 'b'");
        await session.ExecuteAsync("CREATE DOMAIN ch_check AS CHAR(3) CHECK ('a ' IN (text_b(), VALUE))");
        await session.ExecuteAsync("CREATE TABLE d(c ch_check)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO d VALUES ('a')"));

        // Assert: the common TEXT group compares 'a ' with 'b' and trimmed 'a'.
        // Domain rejection: PostgreSQL execExprInterp.c:4498-4508 (ExecEvalConstraintCheck).
        failure.Message.ShouldContain("DOMAIN constraint 'ch_check'");
        (await RowsAsync(session, "SELECT c FROM d")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Domain IN checks reject UPDATE, function defaults and function arguments")]
    [InlineData("UPDATE d SET c = 'a'")]
    [InlineData("INSERT INTO d (id) VALUES (2)")]
    [InlineData("SELECT domain_arg('a') FROM d")]
    [InlineData("SELECT CAST('a' AS ch_check) FROM d")]
    public async Task In_DomainValueCallers_ShouldRejectInvalidValue(string statement)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-domain-callers", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION text_b() RETURNS TEXT IMMUTABLE RETURN 'b'");
        await session.ExecuteAsync("CREATE FUNCTION char_a() RETURNS CHAR(3) IMMUTABLE RETURN 'a'");
        await session.ExecuteAsync("CREATE DOMAIN ch_check AS CHAR(3) CHECK ('a ' IN (text_b(), VALUE))");
        await session.ExecuteAsync("CREATE FUNCTION domain_arg(p ch_check) RETURNS TEXT IMMUTABLE RETURN 'ok'");
        await session.ExecuteAsync("CREATE TABLE d (id INT, c ch_check DEFAULT char_a())");
        await session.ExecuteAsync("INSERT INTO d VALUES (1, NULL)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => RowsAsyncOrExecuteAsync(session, statement));

        // Assert: NULL satisfies CHECK; every assignment/conversion checks the non-NULL value.
        // PostgreSQL parse_coerce.c:699-723 constructs domain coercion; execExprInterp.c:4498-4508 rejects FALSE only.
        failure.Message.ShouldContain("ch_check");
        (await RowsAsync(session, "SELECT id, c FROM d")).ShouldBe([[1, null]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Table CHECK distinguishes its columns from called SQL function parameters")]
    [InlineData("in_arg(c)", false)]
    [InlineData("'a ' IN (text_b(), c)", true)]
    [InlineData("'a ' IN (text_b(), char_echo(c))", true)]
    public async Task In_TableCheck_ShouldUseEachBindingScope(string check, bool accepts)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-check-in", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION text_b() RETURNS TEXT IMMUTABLE RETURN 'b'");
        await session.ExecuteAsync("CREATE FUNCTION char_echo(p CHAR(3)) RETURNS CHAR(3) IMMUTABLE RETURN p");
        await session.ExecuteAsync("CREATE FUNCTION in_arg(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN 'a ' IN (text_b(), p)");
        await session.ExecuteAsync($"CREATE TABLE t (c CHAR(3), CONSTRAINT ck_in CHECK ({check}))");

        // Act / Assert: Var candidates compare independently (parse_expr.c:1248-1288);
        // an unknown partner takes CHAR (parse_oper.c:272-286). CHECK rejects FALSE (execMain.c:1889-1904,2131-2136).
        if (accepts)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES ('a')");
            (await RowsAsync(session, "SELECT c FROM t")).ShouldBe([["a  "]]);
        }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO t VALUES ('a')"));
            failure.Message.ShouldContain("ck_in");
            (await RowsAsync(session, "SELECT c FROM t")).ShouldBeEmpty();
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL function IN results stay false in DEFAULT and nested function callers")]
    public async Task In_FunctionDefault_ShouldStoreFalse()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-default-in", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION in_arg(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN 'a ' IN (CAST('b' AS TEXT), p)");
        await session.ExecuteAsync("CREATE FUNCTION in_wrapper(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN in_arg(p)");
        await session.ExecuteAsync("CREATE TABLE t (id INT, direct BOOLEAN DEFAULT in_arg('a'), nested BOOLEAN DEFAULT in_wrapper('a'))");

        // Act
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2)");
        var rows = await RowsAsync(session, "SELECT direct, nested, in_wrapper('a') FROM t ORDER BY id");

        // Assert: the same TEXT-first comparison is evaluated as the DEFAULT expression.
        // PostgreSQL rewriteHandler.c:1277-1331 obtains and coerces the column DEFAULT expression.
        rows.ShouldBe([[false, false, false], [false, false, false]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Domain VALUE BETWEEN bounds retain independent comparison types")]
    [InlineData("VALUE", "text_a()", false)]
    [InlineData("text_a()", "VALUE", true)]
    [InlineData("char_echo(VALUE)", "text_a()", false)]
    [InlineData("text_a()", "char_echo(VALUE)", true)]
    public async Task Between_DomainValue_ShouldBindComparisonsIndependently(string low, string high, bool accepts)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-domain-between", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION text_a() RETURNS TEXT IMMUTABLE RETURN 'a'");
        await session.ExecuteAsync("CREATE FUNCTION char_echo(p CHAR(3)) RETURNS CHAR(3) IMMUTABLE RETURN p");
        await session.ExecuteAsync($"CREATE DOMAIN ch_between AS CHAR(3) CHECK ('a ' BETWEEN {low} AND {high})");
        await session.ExecuteAsync("CREATE TABLE t (c ch_between)");

        // Act / Assert: PostgreSQL parse_expr.c:1325-1341 expands BETWEEN into two operators;
        // parse_oper.c:272-286 types each unknown separately, so the TEXT upper bound rejects 'a '.
        if (accepts)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES ('a')");
            (await RowsAsync(session, "SELECT c FROM t")).ShouldBe([["a  "]]);
        }
        else
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO t VALUES ('a')"));
            failure.Message.ShouldContain("ch_between");
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - One IN list composes table columns, SQL parameter results and domain VALUE checks")]
    public async Task In_TableFunctionAndDomainCandidates_ShouldKeepBindingOrigins()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-mixed-scopes", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION text_b() RETURNS TEXT IMMUTABLE RETURN 'b'");
        await session.ExecuteAsync("CREATE FUNCTION char_result(p CHAR(3)) RETURNS CHAR(3) IMMUTABLE RETURN CASE WHEN 'a ' NOT IN (text_b(), p) THEN p ELSE 'bad' END");
        await session.ExecuteAsync("CREATE DOMAIN ch_ok AS CHAR(3) CHECK ('a ' NOT IN (text_b(), VALUE))");
        // A real column named VALUE must still be a table reference.
        await session.ExecuteAsync("CREATE TABLE t (id INT, c CHAR(3), VALUE CHAR(3))");
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 'a', 'a'), (2, 'b', 'b')");
        const string candidates = "text_b(), char_result('a'), CAST('a' AS ch_ok)";

        // Act
        var rows = await RowsAsync(session, $"SELECT 'a ' IN ({candidates}, c), 'a ' NOT IN ({candidates}, c), " +
            $"'a ' IN ({candidates}), 'a ' IN ({candidates}, VALUE) FROM t ORDER BY id");

        // Assert: the non-Var TEXT group is false, and the independent table CHAR comparison decides.
        // PostgreSQL parse_expr.c:1163-1190,1248-1288; parameter/domain identities cited above.
        rows.ShouldBe([[true, false, false, true], [false, true, false, false]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL parameter IN binding gives the same predicate result through a scan and a seek")]
    public async Task In_FunctionSeekBound_ShouldMatchScan()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-in-seek", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION in_arg(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN 'a ' IN (CAST('b' AS TEXT), p)");
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2)");
        const string query = "SELECT id FROM t WHERE id = CASE WHEN in_arg('a') THEN 1 ELSE 2 END";

        // Act
        var scanned = await RowsAsync(session, query);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
        await session.ExecuteAsync("CREATE INDEX ix_id ON t(id)");
        var indexed = await RowsAsync(session, query);

        // Assert: TEXT-first IN is false regardless of access path; CASE chooses ELSE
        // (PostgreSQL execExpr.c:1813-1879). Access-path names are Cohesion instrumentation.
        scanned.ShouldBe([[2]]);
        indexed.ShouldBe(scanned);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_id");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Reopened CHAR definitions retain function and domain binding contexts")]
    public async Task Open_FunctionAndDomainChecks_ShouldRetainBindingContexts()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-character-binding", Guid.NewGuid().ToString("N"));
        try
        {
            for (int phase = 0; phase < 2; phase++)
            {
                await using var engine = SqlDatabaseEngine.Create("char-binding-reopen", new SqlDatabaseEngineOptions { RootPath = root });
                var database = phase == 0 ? await engine.CreateDatabaseAsync("chars") : await engine.OpenDatabaseAsync("chars");
                await using var session = await database.CreateSessionAsync();
                if (phase == 0)
                {
                    await session.ExecuteAsync("CREATE FUNCTION text_b() RETURNS TEXT IMMUTABLE RETURN 'b'");
                    await session.ExecuteAsync("CREATE FUNCTION in_arg(p CHAR(3)) RETURNS BOOLEAN IMMUTABLE RETURN 'a ' IN (text_b(), p)");
                    await session.ExecuteAsync("CREATE DOMAIN ch_check AS CHAR(3) CHECK ('a ' IN (text_b(), VALUE))");
                    await session.ExecuteAsync("CREATE TABLE d (c ch_check)");
                    await session.ExecuteAsync("CREATE TABLE checked (c CHAR(3), CONSTRAINT ck_in CHECK (in_arg(c)))");
                    await session.ExecuteAsync("CREATE TABLE defaults (id INT, v BOOLEAN DEFAULT in_arg('a'))");
                }

                // Act
                await session.ExecuteAsync($"INSERT INTO defaults (id) VALUES ({phase})");
                var rows = await RowsAsync(session, $"SELECT v, in_arg('a') FROM defaults WHERE id = {phase}");

                // Assert: the TEXT-first result and both constraint rejections are unchanged on reload.
                // PostgreSQL Param/CoerceToDomainValue and IN sources above; domain CHECK
                // execExprInterp.c:4498-4508; table CHECK execMain.c:1889-1904,2131-2136.
                rows.ShouldBe([[false, false]]);
                var domainFailure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO d VALUES ('a')"));
                domainFailure.Message.ShouldContain("ch_check");
                var checkFailure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO checked VALUES ('a')"));
                checkFailure.Message.ShouldContain("ck_in");
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    private static async Task RowsAsyncOrExecuteAsync(SqlDatabaseSession session, string sql)
    {
        if (sql.StartsWith("SELECT", System.StringComparison.Ordinal))
        {
            await RowsAsync(session, sql);
        }
        else
        {
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
