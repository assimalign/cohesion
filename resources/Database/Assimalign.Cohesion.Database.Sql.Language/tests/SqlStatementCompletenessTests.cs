using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Statement completeness (#1068): a statement is parsed in full or rejected. Text the
/// parser did not consume never disappears, because executing the consumed prefix runs
/// a different statement from the one written.
/// </summary>
public sealed class SqlStatementCompletenessTests
{
    /// <summary>
    /// Complete statement forms keyed by the profile clause they exercise, each ending in
    /// a different parser branch. Every advertised clause and every statement kind needs
    /// at least one form, so a clause added to the profile cannot skip the leftover check.
    /// </summary>
    private static readonly Dictionary<string, string[]> _corpus = new(StringComparer.Ordinal)
    {
        [SqlClauses.Select] = ["SELECT 1", "SELECT a, b AS c FROM t", "SELECT DISTINCT a FROM t"],
        [SqlClauses.From] = ["SELECT * FROM t", "SELECT * FROM dbo.t AS u", "SELECT * FROM t u"],
        [SqlClauses.Join] = ["SELECT * FROM t JOIN u ON t.id = u.id", "SELECT * FROM t INNER JOIN u v ON t.id = v.id"],
        [SqlClauses.Where] =
        [
            "SELECT * FROM t WHERE a = 1",
            "SELECT * FROM t WHERE a IS NOT NULL",
            "SELECT * FROM t WHERE a LIKE 'x%'",
            "SELECT * FROM t WHERE a NOT BETWEEN 1 AND 2",
            "SELECT * FROM t WHERE a NOT IN (1, 2)",
            "SELECT * FROM t WHERE a = @a OR b = $1",
        ],
        [SqlClauses.GroupBy] = ["SELECT a, COUNT(*) FROM t GROUP BY a", "SELECT a, b FROM t GROUP BY a, b"],
        [SqlClauses.Having] = ["SELECT a FROM t GROUP BY a HAVING COUNT(*) > 1"],
        [SqlClauses.OrderBy] = ["SELECT a FROM t ORDER BY a", "SELECT a FROM t ORDER BY a DESC, 2 ASC"],
        [SqlClauses.Limit] = ["SELECT a FROM t LIMIT 1"],
        [SqlClauses.Offset] = ["SELECT a FROM t OFFSET 1", "SELECT a FROM t ORDER BY a LIMIT 1 OFFSET 2"],
        [SqlClauses.Values] = ["INSERT INTO t VALUES (1), (2)"],
        [SqlClauses.Subquery] =
        [
            "SELECT a FROM t WHERE a IN (SELECT b FROM u)",
            "SELECT (SELECT MAX(b) FROM u) FROM t",
            "SELECT a FROM t WHERE NOT EXISTS (SELECT b FROM u)",
            "INSERT INTO t (a) SELECT b FROM u",
        ],
        [SqlClauses.Case] = ["SELECT CASE WHEN a = 1 THEN 'x' ELSE 'y' END FROM t", "SELECT a FROM t WHERE CASE a WHEN 1 THEN TRUE END"],
        [SqlClauses.Cast] = ["SELECT CAST(a AS INT) FROM t", "SELECT a FROM t WHERE CAST(a AS TEXT) = '1'"],
        [SqlClauses.Collate] = ["SELECT a FROM t WHERE a = 'x' COLLATE binary", "SELECT a COLLATE case_insensitive FROM t"],
        [SqlClauses.Insert] = ["INSERT INTO t VALUES (1)", "INSERT INTO t (a, b) VALUES (1, 'x')"],
        [SqlClauses.Update] = ["UPDATE t SET a = 1", "UPDATE dbo.t SET a = 1, b = 'x' WHERE id = 1"],
        [SqlClauses.Delete] = ["DELETE FROM t", "DELETE FROM t WHERE id = 1"],
        [SqlClauses.CreateTable] = ["CREATE TABLE t (id INT PRIMARY KEY, name TEXT NOT NULL DEFAULT 'x')", "CREATE TABLE IF NOT EXISTS dbo.t (id INT)"],
        [SqlClauses.CreateIndex] = ["CREATE INDEX ix ON t (a)", "CREATE UNIQUE INDEX IF NOT EXISTS ix ON dbo.t (a, b)"],
        [SqlClauses.AlterTable] =
        [
            "ALTER TABLE t ADD COLUMN c INT",
            "ALTER TABLE t ADD c INT DEFAULT 1",
            "ALTER TABLE t DROP COLUMN c",
            "ALTER TABLE t DROP c",
            "ALTER TABLE t ADD CONSTRAINT ck CHECK (a > 0)",
            "ALTER TABLE t DROP CONSTRAINT ck",
        ],
        [SqlClauses.DropTable] = ["DROP TABLE t", "DROP TABLE IF EXISTS dbo.t"],
        [SqlClauses.DropIndex] = ["DROP INDEX ix ON t", "DROP INDEX IF EXISTS ix ON dbo.t"],
        [SqlClauses.Constraint] = ["CREATE TABLE c (a INT, CONSTRAINT uq UNIQUE (a))"],
        [SqlClauses.ForeignKey] = ["CREATE TABLE c (p INT, FOREIGN KEY (p) REFERENCES t (id))"],
        [SqlClauses.References] = ["CREATE TABLE c (p INT REFERENCES t (id))"],
        [SqlClauses.Check] = ["CREATE TABLE c (a INT CHECK (a > 0))"],
        [SqlClauses.UniqueConstraint] = ["CREATE TABLE c (a INT UNIQUE)"],
        [SqlClauses.Cascade] = ["CREATE TABLE c (p INT REFERENCES t (id) ON DELETE CASCADE)"],
        [SqlClauses.Restrict] =
        [
            "CREATE TABLE c (p INT REFERENCES t (id) ON DELETE RESTRICT)",
            "ALTER TABLE c ADD CONSTRAINT fk FOREIGN KEY (p) REFERENCES t (id) ON DELETE RESTRICT",
        ],
        [SqlClauses.Begin] = ["BEGIN"],
        [SqlClauses.Commit] = ["COMMIT", "COMMIT TRANSACTION"],
        [SqlClauses.Rollback] = ["ROLLBACK", "ROLLBACK TRANSACTION"],
        [SqlClauses.Transaction] = ["BEGIN TRANSACTION"],
    };

    /// <summary>
    /// Leftovers appended to every complete form: words (the first may be read as an
    /// alias), a literal, a stray parenthesis, a misspelled clause, and text after the
    /// terminating ';' — including a second statement.
    /// </summary>
    private static readonly string[] _leftovers = [" x y", " 1", " )", " WHRE id = 1", "; x", "; DELETE FROM t", ";;"];

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: every advertised clause and statement kind has a corpus form")]
    public void Corpus_EveryClauseAndStatementKind_ShouldHaveACompleteForm()
    {
        // Act / Assert
        RequireCoverage(SqlLanguageProfile.Instance.Clauses, StatementKinds(), _corpus);
        _corpus.Keys.Except(SqlLanguageProfile.Instance.Clauses).ShouldBeEmpty("Corpus keys must name current profile clauses.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: a clause added without a corpus form fails coverage")]
    public void Corpus_ClauseWithoutForm_ShouldFailCoverage()
    {
        // Arrange
        var clauses = SqlLanguageProfile.Instance.Clauses.Append("FUTURE SQL CLAUSE").ToArray();

        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireCoverage(clauses, StatementKinds(), _corpus))
            .Message.ShouldContain("FUTURE SQL CLAUSE", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: a statement kind added without a corpus form fails coverage")]
    public void Corpus_StatementKindWithoutForm_ShouldFailCoverage()
    {
        // Arrange
        var kinds = StatementKinds().Append((SqlQueryCommandType)999).ToArray();

        // Act / Assert
        Should.Throw<ShouldAssertException>(() => RequireCoverage(SqlLanguageProfile.Instance.Clauses, kinds, _corpus))
            .Message.ShouldContain("999", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: complete forms parse with or without one terminating ';'")]
    public void Parse_CompleteForm_ShouldAcceptOneOptionalTerminator()
    {
        // Arrange
        var failures = new List<string>();

        // Act
        foreach (string form in _corpus.Values.SelectMany(forms => forms))
        {
            foreach (string sql in new[] { form, form + ";", form + "; -- done", form + " /* end */ ;" })
            {
                var errors = Errors(Parse(sql));
                if (errors.Length > 0)
                {
                    failures.Add($"{sql} => {string.Join(" | ", errors.Select(error => $"{error.Code}: {error.Message}"))}");
                }
            }
        }

        // Assert
        failures.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: no statement kind ends with unconsumed tokens")]
    public void Parse_CompleteFormFollowedByLeftovers_ShouldReportAnErrorAfterTheForm()
    {
        // Arrange
        var failures = new List<string>();

        // Act
        foreach (string form in _corpus.Values.SelectMany(forms => forms))
        {
            foreach (string leftover in _leftovers)
            {
                string sql = form + leftover;
                var statement = Should.NotThrow(() => Parse(sql));
                if (!Errors(statement).Any(error => error.Start >= form.Length))
                {
                    failures.Add(sql);
                }
            }
        }

        // Assert
        failures.ShouldBeEmpty("Each statement must report an error at or after the end of its complete form.");
    }

    /// <summary>The issue's regression cases: each is one SQL0003 at the dropped text.</summary>
    /// <param name="marked">A statement whose tail the parser used to drop; '^' marks where the diagnostic starts.</param>
    /// <param name="located">The source text the diagnostic covers.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: dropped clauses and predicates report SQL0003 where they start")]
    [InlineData("DELETE FROM t WHRE ^id = 1;", "id")]
    [InlineData("UPDATE t SET a = 1 ^WHRE id = 1;", "WHRE")]
    [InlineData("UPDATE t SET a = 1 ^FROM u WHERE t.id = u.id;", "FROM")]
    [InlineData("INSERT INTO t (id) VALUES (1) ^ON CONFLICT DO NOTHING;", "ON")]
    [InlineData("INSERT INTO t (id, a) VALUES (1, 2) ^ON DUPLICATE KEY UPDATE a = 3;", "ON")]
    [InlineData("SELECT id FROM t ORDER BY id OFFSET 1 ^LIMIT 2;", "LIMIT")]
    [InlineData("SELECT id FROM t WHERE name LIKE 'a!%' ^ESCAPE '!';", "ESCAPE")]
    [InlineData("SELECT id FROM t WHERE a ^IS DISTINCT FROM b;", "IS DISTINCT FROM")]
    [InlineData("SELECT id FROM t WHERE a ^IS NOT DISTINCT FROM b;", "IS NOT DISTINCT FROM")]
    [InlineData("SELECT id FROM t WHERE flag ^IS TRUE;", "IS TRUE")]
    [InlineData("SELECT id FROM t WHERE flag ^IS NOT FALSE;", "IS NOT FALSE")]
    [InlineData("SELECT id FROM t WHERE flag ^IS UNKNOWN;", "IS UNKNOWN")]
    [InlineData("SELECT flag ^IS TRUE FROM t;", "IS TRUE")]
    [InlineData("SELECT name LIKE 'a%' ^ESCAPE '!' FROM t;", "ESCAPE")]
    [InlineData("SELECT id FROM t WHERE id = ^:id;", ":")]
    [InlineData("UPDATE t SET a = ^:a WHERE id = 1;", ":")]
    [InlineData("DELETE FROM t WHERE id = 1; ^DELETE FROM t;", "DELETE")]
    public void Parse_DroppedTail_ShouldReportOneSyntaxErrorAtIt(string marked, string located)
    {
        // Arrange
        int start = marked.IndexOf('^', StringComparison.Ordinal);
        string sql = marked.Remove(start, 1);

        // Act
        var statement = Parse(sql);

        // Assert
        var error = Errors(statement).ShouldHaveSingleItem();
        error.Code.ShouldBe("SQL0003");
        error.Start.ShouldBe(start);
        sql[error.Start!.Value..error.End!.Value].ShouldBe(located);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: a misspelled keyword read as an alias is named in the diagnostic")]
    public void Parse_MisspelledWhereReadAsAlias_ShouldNameTheAlias()
    {
        // Act
        var statement = Parse("DELETE FROM t WHRE id = 1;");

        // Assert
        var error = Errors(statement).ShouldHaveSingleItem();
        error.Message.ShouldBe("Unexpected 'id' after the end of the DELETE statement. 'WHRE' was read as an alias of table 't'.");
        statement.SqlExpression.ShouldBeOfType<SqlDeleteExpression>().Where.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: text after the terminator is a second statement")]
    [InlineData("SELECT 1; SELECT 2;")]
    [InlineData("BEGIN; DELETE FROM t;")]
    [InlineData("COMMIT /* done */ ; -- next\n ROLLBACK;")]
    [InlineData("COMMIT;;")]
    public void Parse_TextAfterTerminator_ShouldReportExactlyOneStatement(string sql)
    {
        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("SQL0003");
        error.Message!.ShouldContain("after ';'. A request accepts exactly one statement.", Case.Sensitive);
        error.Start!.Value.ShouldBeGreaterThan(sql.IndexOf(';', StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: a missing expression is an error, never a NULL literal")]
    [InlineData("SELECT id FROM t WHERE id =;", "Expected an expression but found ';'.", ";")]
    [InlineData("UPDATE t SET a =", "Expected an expression before the end of the statement.", "")]
    [InlineData("DELETE FROM t WHERE", "Expected an expression before the end of the statement.", "")]
    [InlineData("SELECT +a FROM t;", "Expected an expression but found '+'.", "+")]
    [InlineData("SELECT id FROM t WHERE a IS;", "Expected NULL after IS.", ";")]
    public void Parse_MissingExpression_ShouldReportSyntaxError(string sql, string message, string located)
    {
        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("SQL0003");
        error.Message.ShouldBe(message);
        sql[error.Start!.Value..error.End!.Value].ShouldBe(located);
    }

    /// <summary>An unsupported ALTER TABLE action is named and builds no action node.</summary>
    /// <param name="sql">The ALTER TABLE statement.</param>
    /// <param name="action">The action the diagnostic names.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: unsupported ALTER TABLE actions are named at parse time")]
    [InlineData("ALTER TABLE t RENAME TO u;", "RENAME TO")]
    [InlineData("ALTER TABLE t RENAME COLUMN a TO b;", "RENAME COLUMN")]
    [InlineData("ALTER TABLE t ALTER COLUMN a TYPE BIGINT;", "ALTER COLUMN")]
    [InlineData("ALTER TABLE t MODIFY a BIGINT;", "MODIFY")]
    [InlineData("alter table t set schema s;", "SET")]
    public void Parse_UnsupportedAlterTableAction_ShouldNameItWithoutAStubNode(string sql, string action)
    {
        // Act
        var statement = Parse(sql);

        // Assert
        var error = Errors(statement).ShouldHaveSingleItem();
        error.Code.ShouldBe("SQL0003");
        error.Message!.ShouldContain($"'{action}'", Case.Sensitive);
        sql[error.Start!.Value..error.End!.Value].ToUpperInvariant().ShouldBe(action);
        statement.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Alter);
        statement.SqlExpression.ShouldNotBeOfType<SqlAlterTableExpression>();
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "SQL0002");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: ALTER TABLE without a complete action is an error")]
    [InlineData("ALTER TABLE t;", "ALTER TABLE requires an action")]
    [InlineData("ALTER TABLE t DROP;", "Expected a column name")]
    [InlineData("ALTER TABLE t DROP CONSTRAINT;", "Expected a constraint name")]
    [InlineData("ALTER TABLE t ADD COLUMN;", "Expected a column definition")]
    public void Parse_IncompleteAlterTableAction_ShouldReportSyntaxError(string sql, string message)
    {
        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("SQL0003");
        error.Message!.ShouldContain(message, Case.Sensitive);
    }

    private static void RequireCoverage(
        IEnumerable<string> clauses,
        IEnumerable<SqlQueryCommandType> kinds,
        IReadOnlyDictionary<string, string[]> corpus)
    {
        clauses.Where(clause => !corpus.ContainsKey(clause))
            .ShouldBeEmpty("Every advertised clause needs a complete statement form in the completeness corpus.");

        var produced = corpus.Values.SelectMany(forms => forms)
            .Select(form => Parse(form).SqlExpression.CommandType)
            .ToHashSet();
        kinds.Where(kind => !produced.Contains(kind))
            .ShouldBeEmpty("Every statement kind needs a complete statement form in the completeness corpus.");
    }

    private static SqlQueryCommandType[] StatementKinds()
        => Enum.GetValues<SqlQueryCommandType>().Where(kind => kind != SqlQueryCommandType.Unknown).ToArray();

    private static Diagnostic[] Errors(SqlQueryStatement statement)
        => statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);
}
