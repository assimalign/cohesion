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
    /// alias), a literal, a stray parenthesis, a misspelled clause, text after the
    /// terminating ';' — including a second statement — an unterminated string or block
    /// comment that would swallow the rest, a character outside the dialect, and words on
    /// the line after a <c>--</c> comment for every line terminator, which the comment used
    /// to swallow unless the line ended in LF (#1150).
    /// </summary>
    private static readonly string[] _leftovers =
    [
        " x y", " 1", " )", " WHRE id = 1", "; x", "; DELETE FROM t", ";;", " 'x", " /* x", " # x",
        " -- c\n x y", " -- c\r x y", " -- c\r\n x y", " -- c" + (char)0x0085 + " x y",
        " -- c" + (char)0x2028 + " x y", " -- c" + (char)0x2029 + " x y",
    ];

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
    [InlineData("UPDATE t SET name = ^'abc WHERE id = 1;", "'")]
    [InlineData("UPDATE t SET name = ^'it''s WHERE id = 1;", "'")]
    [InlineData("DELETE FROM t ^/* WHERE id = 1;", "/*")]
    [InlineData("DELETE FROM t ^/* /* nested */ WHERE id = 1;", "/*")]
    [InlineData("SELECT ^\"id FROM t;", "\"")]
    [InlineData("DELETE FROM t ^# WHERE id = 1;", "#")]
    [InlineData("DELETE FROM t WHERE a NOT ^NULL;", "NULL")]
    [InlineData("UPDATE t SET age = 0 WHERE flag NOT ^FALSE;", "FALSE")]
    [InlineData("DELETE FROM t WHERE NOT (flag NOT ^NULL);", "NULL")]
    [InlineData("SELECT id FROM t WHERE NOT flag NOT ^TRUE;", "TRUE")]
    [InlineData("SELECT a NOT ^NULL FROM t;", "NULL")]
    public void Parse_DroppedTail_ShouldReportOneSyntaxErrorAtIt(string marked, string located)
        => AssertOneSyntaxErrorAt(marked, located);

    /// <summary>
    /// A missing token, keyword or name is reported where it was expected, once, instead of
    /// being skipped or replaced by a placeholder that the statement then executes.
    /// </summary>
    /// <param name="marked">A malformed statement; '^' marks where the diagnostic starts.</param>
    /// <param name="located">The source text the diagnostic covers.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: missing tokens, keywords and names report one SQL0003 where expected")]
    [InlineData("DELETE FROM t WHERE (id = 1^;", ";")]
    [InlineData("DELETE FROM t WHERE id IN (1, 2^;", ";")]
    [InlineData("DELETE FROM t WHERE id IN ^1;", "1")]
    [InlineData("DELETE FROM t WHERE id BETWEEN 1 ^2;", "2")]
    [InlineData("UPDATE t SET name = CASE WHEN id = 1 ^'a' END WHERE id = 1;", "'a'")]
    [InlineData("UPDATE t SET name = CASE WHEN id = 1 THEN 'a' ^WHERE id = 1;", "WHERE")]
    [InlineData("SELECT id FROM t ORDER ^id;", "id")]
    [InlineData("SELECT COUNT(id ^FROM t;", "FROM")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT id FROM u^;", ";")]
    [InlineData("UPDATE t SET name = 'z', ^WHERE id = 1;", "WHERE")]
    [InlineData("UPDATE t SET name ^'q' WHERE id = 1;", "'q'")]
    [InlineData("UPDATE t SET ^= 1 WHERE id = 1;", "=")]
    [InlineData("UPDATE t SET name ^WHERE id = 1;", "WHERE")]
    [InlineData("UPDATE t^;", ";")]
    [InlineData("DELETE^;", ";")]
    [InlineData("DELETE FROM ^WHERE id = 1;", "WHERE")]
    [InlineData("SELECT 1 FROM^;", ";")]
    [InlineData("SELECT * FROM t JOIN ^ON t.id = 1;", "ON")]
    [InlineData("SELECT * FROM t AS^;", ";")]
    [InlineData("INSERT INTO t VALUES (1), (^);", ")")]
    [InlineData("INSERT INTO t VALUES (1),^;", ";")]
    [InlineData("INSERT INTO t (id, name,^, age) VALUES (5, 'x', 1);", ",")]
    [InlineData("INSERT INTO t (^) VALUES (1);", ")")]
    [InlineData("INSERT INTO t (id) ^DEFAULT VALUES;", "DEFAULT")]
    [InlineData("CREATE INDEX ^ON t (name);", "ON")]
    [InlineData("CREATE INDEX ix ON t (name,^, age);", ",")]
    [InlineData("CREATE INDEX ix ON t^;", ";")]
    [InlineData("DROP INDEX ix ^t;", "t")]
    [InlineData("DROP TABLE IF ^t;", "t")]
    [InlineData("DROP TABLE IF ^NOT EXISTS t;", "NOT")]
    [InlineData("CREATE INDEX IF ^EXISTS ix ON t (name);", "EXISTS")]
    [InlineData("CREATE INDEX IF NOT ^ix ON t (name);", "ix")]
    [InlineData("CREATE TABLE IF NOT ^x (a INT);", "x")]
    [InlineData("CREATE TABLE ^(a INT);", "(")]
    [InlineData("CREATE TABLE z (a TEXT NOT^);", ")")]
    [InlineData("CREATE TABLE z (a INT,^);", ")")]
    [InlineData("CREATE TABLE z (a^);", ")")]
    [InlineData("CREATE TABLE z (a VARCHAR(25 ^5));", "5")]
    [InlineData("CREATE TABLE z (b DECIMAL(10, 2^, 5));", ",")]
    [InlineData("CREATE TABLE z (c VARCHAR(^-5));", "-")]
    [InlineData("CREATE TABLE z (a VARCHAR(^MAX));", "MAX")]
    [InlineData("CREATE TABLE z (a VARCHAR(^99999999999));", "99999999999")]
    [InlineData("ALTER TABLE t ADD COLUMN c VARCHAR(^1e3);", "1e3")]
    [InlineData("ALTER ^INDEX ix RENAME TO iy;", "INDEX")]
    [InlineData("ALTER ^t ADD COLUMN c INT;", "t")]
    [InlineData("ALTER TABLE ^ADD COLUMN c INT;", "ADD")]
    [InlineData("CREATE TABLE c (a INT DEFAULT ^:x);", ":")]
    [InlineData("CREATE TABLE c (a INT DEFAULT 1 ^FOO BAR, b INT);", "FOO")]
    [InlineData("SELECT a ^IS DISTINCT FROM;", "IS DISTINCT FROM")]
    [InlineData("DELETE FROM t WHERE id = 1 AND name LIKE 'x' ^ESCAPE;", "ESCAPE")]
    public void Parse_MissingToken_ShouldReportOneSyntaxErrorWhereExpected(string marked, string located)
        => AssertOneSyntaxErrorAt(marked, located);

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: a second statement after the terminator is reported separately")]
    public void Parse_UnclosedTypeArgumentsFollowedByStatement_ShouldReportBoth()
    {
        // Act
        var errors = Errors(Parse("CREATE TABLE z (a VARCHAR(10; DROP TABLE t));"));

        // Assert
        errors.Select(error => error.Message).ShouldBe(
        [
            "Expected ')' after the type arguments but found ';'.",
            "Unexpected 'DROP' after ';'. A request accepts exactly one statement.",
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: type arguments are normalized integer literals")]
    public void Parse_TypeArguments_ShouldNormalizeToIntegerLiterals()
    {
        // Act
        var statement = Parse("CREATE TABLE z (a VARCHAR( 10 ), b DECIMAL(18 , 4), c INT);");

        // Assert
        Errors(statement).ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Columns
            .Select(column => column.DataType).ShouldBe(["VARCHAR(10)", "DECIMAL(18,4)", "INT"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: terminated literals, quoted identifiers and comments parse")]
    public void Parse_TerminatedLexemes_ShouldReportNoError()
    {
        // Act
        var statement = Parse("SELECT 'it''s', '''' /* a /* nested */ b */, \"id\" FROM t -- done\n;");

        // Assert
        Errors(statement).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: an unterminated comment or a stray character is an error on its own")]
    [InlineData("/* only a comment", "Unterminated block comment")]
    [InlineData("SELECT 1 \U0001F643;", "Unexpected character U+1F643")]
    [InlineData("SELECT 1 ​;", "Unexpected character U+200B")]
    public void Parse_LexicalError_ShouldReportSyntaxError(string sql, string message)
    {
        // Act
        var errors = Errors(Parse(sql));

        // Assert
        errors.Where(error => error.Code == "SQL0003").ShouldHaveSingleItem().Message!.ShouldStartWith(message, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Completeness: a sign applies to a signed operand")]
    public void Parse_NestedUnaryMinus_ShouldNestTheOperators()
    {
        // Act
        var statement = Parse("SELECT - -1, - -a FROM t;");

        // Assert
        Errors(statement).ShouldBeEmpty();
        var columns = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns;
        var negate = columns[0].Expression.ShouldBeOfType<SqlUnaryExpression>();
        negate.Operator.ShouldBe(SqlUnaryOperator.Negate);
        negate.Operand.ShouldBeOfType<SqlUnaryExpression>().Operator.ShouldBe(SqlUnaryOperator.Negate);
        columns[1].Expression.ShouldBeOfType<SqlUnaryExpression>().Operand.ShouldBeOfType<SqlUnaryExpression>()
            .Operator.ShouldBe(SqlUnaryOperator.Negate);

        // ~ is outside the dialect since #1101; its signed operand still leaves no text behind.
        Errors(Parse("SELECT ~ -a FROM t;")).ShouldHaveSingleItem().Code.ShouldBe("COHDBL001");
    }

    /// <summary>
    /// A recognized clause outside the profile reports COHDBL001, and text from that clause
    /// on adds no second diagnostic. Text the parser stopped at before reaching the clause
    /// still reports SQL0003, so fixing the clause does not reveal a new error.
    /// </summary>
    /// <param name="sql">A statement with a clause outside the profile.</param>
    /// <param name="expected">The error codes, in order.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Completeness: an unsupported clause owns the text after it, not the text before it")]
    [InlineData("DELETE FROM t WHERE id = 1 RETURNING *;", new[] { "COHDBL001" })]
    [InlineData("DELETE FROM t WHERE id = 1 RETURNING *; DELETE FROM t;", new[] { "COHDBL001" })]
    [InlineData("DELETE FROM t WHRE id = 1 RETURNING *;", new[] { "SQL0003", "COHDBL001" })]
    [InlineData("SELECT * FROM (SELECT id FROM u) d ORDER BY id;", new[] { "COHDBL001" })]
    [InlineData("SELECT * FROM t JOIN (SELECT id FROM u) q ON t.id = q.id;", new[] { "COHDBL001" })]
    public void Parse_UnsupportedClause_ShouldOwnOnlyTheTextAfterIt(string sql, string[] expected)
    {
        // Act
        var errors = Errors(Parse(sql));

        // Assert
        errors.Select(error => error.Code).ShouldBe(expected);
    }

    private static void AssertOneSyntaxErrorAt(string marked, string located)
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
    [InlineData("SELECT +", "Expected an expression before the end of the statement.", "")]
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
