using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Regression coverage for CHAR values crossing comparison, assignment and catalog boundaries.</summary>
public sealed class SqlCharacterBoundaryTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Runtime unary operand errors name the CHAR storage type")]
    [InlineData("+")]
    [InlineData("-")]
    public async Task UnarySign_CharacterCaseResult_ShouldNameCharacterType(string sign)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-operand", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (c CHAR(3))");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await RowsAsync(session, $"SELECT {sign}(CASE WHEN TRUE THEN c ELSE NULL END) FROM t"));

        // Assert
        failure.DiagnosticDetail("COHSQLE003").ShouldBe($"Invalid operand type: unary '{sign}' requires a numeric operand, but the operand is Character.");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Native domain checks apply their own CHAR parameter width")]
    [InlineData("INSERT INTO t VALUES ('a')", "SELECT c FROM t")]
    [InlineData("INSERT INTO seed VALUES (1)", "SELECT CAST('a' AS code) FROM seed")]
    [InlineData("INSERT INTO seed VALUES (1)", "SELECT domain_echo(CAST('a' AS CHAR(3))) FROM seed")]
    public async Task DomainCheck_CharacterParameter_ShouldApplyDeclaredWidth(string setup, string query)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("char-domain-check");
        builder.Types.Add(SqlType.CreateDomain("code", SqlType.Char(3), new CharacterWidthCheck()));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (c code)");
        await session.ExecuteAsync("CREATE TABLE seed (id INT)");
        await session.ExecuteAsync("CREATE FUNCTION domain_echo(p code) RETURNS code IMMUTABLE RETURN p");

        // Act
        await session.ExecuteAsync(setup);
        var rows = await RowsAsync(session, query);

        // Assert
        rows.ShouldBe([["a  "]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CHECK permits CHAR function results in supported character comparisons")]
    [InlineData("char_echo(c) = 'a '")]
    [InlineData("char_echo(c) BETWEEN 'a ' AND 'a'")]
    [InlineData("char_echo(c) IN ('a ')")]
    public async Task CreateTable_CharacterFunctionCheck_ShouldAcceptCharacterComparisons(string check)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-check", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE FUNCTION char_echo(p CHAR(3)) RETURNS CHAR(3) IMMUTABLE RETURN p");

        // Act
        (await session.ExecuteAsync($"CREATE TABLE t (c CHAR(3), CONSTRAINT ck_char CHECK ({check}))"))
            .Status.ShouldBe(DatabaseResultStatus.Success);
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");

        // Assert
        (await RowsAsync(session, "SELECT c FROM t")).ShouldBe([["a  "]]);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO t VALUES ('b')"));
        failure.Message.ShouldContain("ck_char");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CHAR joins retain collation-equivalent probes longer than the indexed column")]
    public async Task Join_AccentInsensitiveCharacterKeys_ShouldReturnSameRowsAfterIndexing()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-join", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE longer (c CHAR(2) COLLATE case_accent_insensitive)");
        await session.ExecuteAsync("CREATE TABLE shorter (c CHAR(1) COLLATE case_accent_insensitive)");
        await session.ExecuteAsync("INSERT INTO longer VALUES ('a\u0301')");
        await session.ExecuteAsync("INSERT INTO shorter VALUES ('a')");
        const string query = "SELECT l.c, s.c FROM longer l JOIN shorter s ON l.c = s.c";
        (await RowsAsync(session, query)).ShouldBe([["a\u0301", "a"]]);
        session.LastStatementMetrics.ShouldNotBeNull().JoinAccessPaths.ShouldBe(["join-scan"]);

        // Act
        await session.ExecuteAsync("CREATE INDEX ix_shorter ON shorter(c)");
        var indexed = await RowsAsync(session, query);

        // Assert
        session.LastStatementMetrics.ShouldNotBeNull().JoinAccessPaths.ShouldBe(["join-seek:ix_shorter"]);
        indexed.ShouldBe([["a\u0301", "a"]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - BETWEEN binds mixed CHAR and TEXT comparisons independently")]
    [InlineData("CAST('a' AS CHAR(3))", "CAST('a' AS TEXT)", false, false)]
    [InlineData("CAST('a ' AS TEXT)", "CAST('a' AS CHAR(3))", true, false)]
    [InlineData("CAST('a' AS CHAR(3))", "CAST('a' AS TEXT)", false, true)]
    [InlineData("CAST('a ' AS TEXT)", "CAST('a' AS CHAR(3))", true, true)]
    public async Task Between_MixedCharacterBounds_ShouldMatchSeparateComparisons(string low, string high, bool expected, bool parameter)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-between", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");
        string operand = parameter ? "@p" : "'a '";
        var parameters = new Dictionary<string, object?> { ["p"] = "a " };

        // Act
        var rows = await RowsAsync(session,
            $"SELECT {operand} BETWEEN {low} AND {high}, {operand} >= {low} AND {operand} <= {high}, " +
            $"{operand} NOT BETWEEN {low} AND {high} FROM t", parameters);

        // Assert
        rows.ShouldBe([[expected, expected, !expected]]);
    }

    // PostgreSQL parse_expr.c:1173-1190 puts the left operand first; parse_coerce.c:1397-1456
    // skips unknowns and keeps the first known string type: pg_cast.dat:313-325 is implicit
    // in both directions. bpchareq (varchar.c:746-776) ignores trailing spaces on both sides;
    // texteq (varlena.c:1429-1480) retains them. Parameters here use the dialect's unknown-string rule.
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - IN uses the first known character type among non-column candidates")]
    [InlineData("CAST('b' AS CHAR(3)), CAST('a' AS TEXT)", true)]
    [InlineData("CAST('a' AS TEXT), CAST('b' AS CHAR(3))", false)]
    [InlineData("CAST('a' AS CHAR(3)), CAST('b' AS TEXT)", true)]
    [InlineData("CAST('b' AS TEXT), CAST('a' AS CHAR(3))", false)]
    [InlineData("CAST('a' AS CHAR(3)), CAST('b' AS VARCHAR(3))", true)]
    [InlineData("CAST('b' AS VARCHAR(3)), CAST('a' AS CHAR(3))", false)]
    [InlineData("CAST('a' AS VARCHAR(3)), CAST('b' AS CHAR(3))", false)]
    [InlineData("CAST('b' AS CHAR(3)), CAST('a ' AS TEXT)", true)]
    [InlineData("'b', CAST('a' AS CHAR(3)), CAST('d' AS TEXT)", true)]
    public async Task In_MixedCharacterCandidates_ShouldUseFirstKnownType(string candidates, bool expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-in", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");

        // Act
        var rows = await RowsAsync(session,
            $"SELECT 'a ' IN ({candidates}), 'a ' NOT IN ({candidates}), @p IN ({candidates}) FROM t",
            new Dictionary<string, object?> { ["p"] = "a " });

        // Assert
        rows.ShouldBe([[expected, !expected, expected]]);
    }

    // PostgreSQL parse_expr.c:1182-1190 includes the left operand before the candidates.
    // parse_coerce.c:1428-1456 and pg_cast.dat:313-325 keep CHAR/TEXT/VARCHAR whichever is first.
    // Common coercion uses typmod -1 (parse_coerce.c:1583-1585), not a candidate's declared width.
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - IN considers a typed left operand before non-column candidates")]
    [InlineData("CAST('a' AS CHAR(3))", "'a ', CAST('b' AS TEXT)", true)]
    [InlineData("CAST('a' AS CHAR(3))", "'a ', CAST('b' AS VARCHAR(3))", true)]
    [InlineData("CAST('a ' AS TEXT)", "CAST('a' AS CHAR(3)), 'b'", false)]
    [InlineData("CAST('a ' AS VARCHAR(3))", "CAST('a' AS CHAR(3)), 'b'", false)]
    [InlineData("CAST('a' AS CHAR(3))", "CAST('a ' AS TEXT), CAST('b' AS TEXT)", true)]
    [InlineData("CAST('abc' AS CHAR(3))", "CAST('abc ' AS TEXT), 'other'", true)]
    [InlineData("CAST('abc' AS CHAR(3))", "CAST('abcd' AS TEXT), 'other'", false)]
    public async Task In_TypedCharacterOperand_ShouldUseFirstKnownType(string operand, string candidates, bool expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-in-operand", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");

        // Act
        var rows = await RowsAsync(session, $"SELECT {operand} IN ({candidates}), {operand} NOT IN ({candidates}) FROM t");

        // Assert
        rows.ShouldBe([[expected, !expected]]);
    }

    // PostgreSQL parse_expr.c:1163-1176 excludes every expression containing a level-0 Var;
    // :1273-1279 binds each such comparison with make_op. parse_oper.c:272-286 gives an
    // unknown literal its CHAR partner's type; varchar.c:746-776 then ignores trailing spaces.
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - IN binds column-reference candidates like separate equalities")]
    [InlineData("c")]
    [InlineData("CAST(c AS CHAR(3))")]
    [InlineData("char_echo(c)")]
    [InlineData("c COLLATE binary")]
    [InlineData("CASE WHEN TRUE THEN c ELSE CAST('b' AS CHAR(3)) END")]
    public async Task In_ColumnReferenceCandidate_ShouldMatchSeparateComparisons(string candidate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-in-column", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (c CHAR(3))");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");
        await session.ExecuteAsync("CREATE FUNCTION char_echo(p CHAR(3)) RETURNS CHAR(3) IMMUTABLE RETURN p");

        // Act
        var rows = await RowsAsync(session,
            $"SELECT 'a ' IN ({candidate}, CAST('b' AS TEXT)), 'a ' = {candidate} OR 'a ' = CAST('b' AS TEXT), " +
            $"'a ' IN (CAST('b' AS TEXT), {candidate}), 'a ' NOT IN ({candidate}, CAST('b' AS TEXT)), " +
            $"@p IN ({candidate}, CAST('b' AS TEXT)) FROM t", new Dictionary<string, object?> { ["p"] = "a " });

        // Assert
        rows.ShouldBe([[true, true, true, false, true]]);
    }

    // PostgreSQL parse_expr.c:1173-1176 requires two non-Var candidates for common typing;
    // :1248-1279 leaves Var comparisons independent even alongside that group.
    // Type/space expectations follow parse_coerce.c:1397-1456, pg_cast.dat:313-325,
    // varchar.c:746-776 and varlena.c:1429-1480, as in the ordering tests above.
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - IN keeps the common-type group separate from column comparisons")]
    [InlineData("'a '", "c, CAST('b' AS TEXT), CAST('d' AS TEXT)", true)]
    [InlineData("'a '", "CAST(c AS TEXT), CAST('a' AS CHAR(3)), CAST('b' AS TEXT)", true)]
    [InlineData("CAST('a' AS CHAR(3))", "CAST('a ' AS TEXT)", false)]
    [InlineData("CAST('b' AS CHAR(3))", "c, CAST('b ' AS TEXT)", false)]
    public async Task In_SeparatedCandidateGroups_ShouldResolveComparisonsIndependently(string operand, string candidates, bool expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-in-groups", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (c CHAR(3))");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");

        // Act
        var rows = await RowsAsync(session, $"SELECT {operand} IN ({candidates}), {operand} NOT IN ({candidates}) FROM t");

        // Assert
        rows.ShouldBe([[expected, !expected]]);
    }

    // PostgreSQL parse_coerce.c:1444-1456 resolves all-unknown lists to TEXT; texteq at
    // varlena.c:1429-1480 retains spaces. execExprInterp.c's ExecEvalScalarArrayOp preserves
    // NULL for nonmatches and returns the deciding result for a match (:4054-4074,4165-4185).
    // Mixed column/non-column cases combine these results with OR/AND (parse_expr.c:1282-1295).
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - IN all-unknown lists keep text spaces and SQL NULL results")]
    public async Task In_AllUnknownCandidates_ShouldUseTextAndPreserveNulls()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-in-unknown", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (c CHAR(3))");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");

        // Act
        var rows = await RowsAsync(session,
            "SELECT 'a ' IN ('a', 'b'), 'a ' IN ('a ', 'b'), 'a ' IN ('a', NULL), " +
            "'a ' NOT IN ('a', NULL), NULL IN ('a', 'b'), 'a ' IN (c, 'b', NULL), " +
            "'a ' NOT IN (c, 'b', NULL), 'b' IN (c, 'd', NULL), 'b' NOT IN (c, 'd', NULL) FROM t");

        // Assert
        rows.ShouldBe([[false, true, null, null, null, true, false, null, null]]);
    }

    // Cohesion's validation-only ALTER policy (#1447), not PostgreSQL behavior:
    // PostgreSQL performs the rewrite in tablecmds.c:5897 (ATRewriteTables).
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER rewrite refusals name the column and explicit migration remedy")]
    [InlineData("CHAR(4) NOT NULL DEFAULT 'b'")]
    [InlineData("CHAR(4)")]
    public async Task AlterColumn_CharacterRewrite_ShouldNameColumnAndMigration(string definition)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-alter-diagnostic", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE populated (c CHAR(3))");
        await session.ExecuteAsync("INSERT INTO populated VALUES ('a')");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"ALTER TABLE populated ALTER COLUMN c {definition}"));

        // Assert
        failure.Message.ShouldContain("ALTER COLUMN 'c'", Case.Sensitive);
        failure.Message.ShouldContain("explicit table-copy migration", Case.Sensitive);
        failure.Message.ShouldContain("#1447", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Predicate controls: VARCHAR, domain and decimal bounds do not coerce a shared operand")]
    [InlineData("'a '", "CAST('a' AS VARCHAR(1))", "CAST('a' AS TEXT)", "CAST('b' AS VARCHAR(1))")]
    [InlineData("'a '", "CAST('a' AS short_text)", "CAST('a' AS TEXT)", "CAST('b' AS short_text)")]
    [InlineData("1.24", "CAST(1 AS DECIMAL(2,1))", "CAST(1.2 AS DECIMAL(3,2))", "CAST(2 AS DECIMAL(2,1))")]
    [Trait("Purpose", "Control")]
    public async Task PredicateControl_NonCharacterBounds_ShouldMatchSeparateComparisons(string operand, string low, string high, string other)
    {
        // Arrange: controls for whether the shared-coercion defect exists without CHAR.
        await using var engine = SqlDatabaseEngine.Create("predicate-control", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE DOMAIN short_text AS VARCHAR(1)");
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");

        // Act
        var rows = await RowsAsync(session,
            $"SELECT {operand} BETWEEN {low} AND {high}, {operand} >= {low} AND {operand} <= {high}, " +
            $"{operand} IN ({other}, {high}), {operand} = {other} OR {operand} = {high} FROM t");

        // Assert
        rows.ShouldBe([[false, false, false, false]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ALTER COLUMN rejects CHAR widening that would repad stored rows")]
    public async Task AlterColumn_CharacterWideningWithValues_ShouldRejectRewriteAndPreserveDefinition()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-widen", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (c CHAR(2))");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("ALTER TABLE t ALTER COLUMN c CHAR(4)"));

        // Assert
        failure.Message.ShouldContain("would rewrite existing values");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");
        (await RowsAsync(session, "SELECT c, c LIKE 'a ' FROM t")).ShouldBe([["a ", true], ["a ", true]]);
        (await RowsAsync(session, "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't'"))
            .ShouldBe([[2L]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - ALTER COLUMN validates CHAR nullability by exact padded value")]
    public async Task AlterColumn_CharacterNotNull_ShouldAcceptUnchangedPadding()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("char-nullability", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (c CHAR(2))");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')");

        // Act
        (await session.ExecuteAsync("ALTER TABLE t ALTER COLUMN c CHAR(2) NOT NULL")).Status.ShouldBe(DatabaseResultStatus.Success);

        // Assert
        (await RowsAsync(session, "SELECT c, c LIKE 'a ' FROM t")).ShouldBe([["a ", true]]);
        (await RowsAsync(session, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't'"))
            .ShouldBe([["NO"]]);
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT INTO t VALUES (NULL)"));
        var shrink = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("ALTER TABLE t ALTER COLUMN c CHAR(1) NOT NULL"));
        shrink.Message.ShouldContain("would rewrite existing values");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Dropped CHAR domain function signatures validate on cold script reopen")]
    [InlineData(SqlProvisioningMode.Apply)]
    [InlineData(SqlProvisioningMode.Verify)]
    public async Task BuildAsync_DroppedCharacterDomainScript_ShouldValidateHistoricalSignature(SqlProvisioningMode mode)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-character-script", Guid.NewGuid().ToString("N"));
        const string sql = "CREATE DOMAIN code AS CHAR(3); CREATE FUNCTION project(p code) RETURNS CHAR(3) RETURN p; " +
            "DROP FUNCTION project(code); DROP DOMAIN code;";
        SqlDatabaseEngineBuilder Builder(SqlProvisioningMode provisioning)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("char-script");
            builder.Options.RootPath = root;
            builder.AddDatabase("chars", database =>
            {
                database.Provisioning = provisioning;
                database.Script("001", sql);
            });
            return builder;
        }
        try
        {
            await using (var first = await Builder(SqlProvisioningMode.Apply).BuildAsync())
            {
                var database = await first.OpenDatabaseAsync("chars");
                await using var session = await database.CreateSessionAsync();
                // Force receipt reconciliation instead of the matching-generation O(1) path.
                await session.ExecuteAsync("CREATE TABLE changed_generation (id INT)");
            }

            // Act
            await using var reopened = await Builder(mode).BuildAsync();

            // Assert
            var reopenedDatabase = await reopened.OpenDatabaseAsync("chars");
            reopenedDatabase.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(4);
            await using var reopenedSession = await reopenedDatabase.CreateSessionAsync();
            (await RowsAsync(reopenedSession, "SELECT DOMAIN_NAME FROM COHESION_SCHEMA.DOMAINS WHERE DOMAIN_NAME = 'code'"))
                .ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Native CHAR aggregates enforce bounds on already typed arguments")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Aggregate_OverlongCharacterArgument_ShouldRejectAssignment(int arity)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("char-aggregate-overflow");
        builder.Functions.Add(new ObservedCharacterAggregate(arity, 1));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");
        string arguments = string.Join(", ", Enumerable.Repeat("CAST('ab' AS CHAR(2))", arity));

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, $"SELECT observed_char({arguments}) FROM t"));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE012");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Native CHAR aggregates repad already typed arguments to their declared lengths")]
    [InlineData(1, 1, "a")]
    [InlineData(2, 1, "a")]
    [InlineData(5, 1, "a")]
    [InlineData(1, 5, "a    ")]
    [InlineData(2, 5, "a    ")]
    [InlineData(5, 5, "a    ")]
    public async Task Aggregate_CharacterArgumentWithDifferentLength_ShouldApplyParameterPadding(int arity, int length, string expected)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("char-aggregate-padding");
        builder.Functions.Add(new ObservedCharacterAggregate(arity, length));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");
        string arguments = string.Join(", ", Enumerable.Repeat("CAST('a' AS CHAR(3))", arity));

        // Act
        var rows = await RowsAsync(session, $"SELECT observed_char({arguments}) FROM t");

        // Assert
        rows.ShouldBe([[string.Join("|", Enumerable.Repeat(expected, arity))]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Native CHAR aggregates validate arguments before skipping a strict NULL row")]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Aggregate_OverlongCharacterBesideNull_ShouldRejectBeforeStrictDispatch(int arity)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("char-aggregate-strict");
        builder.Functions.Add(new ObservedCharacterAggregate(arity, 1));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("chars");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");
        string arguments = "NULL, " + string.Join(", ", Enumerable.Repeat("CAST('ab' AS CHAR(2))", arity - 1));

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, $"SELECT observed_char({arguments}) FROM t"));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE012");
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
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
