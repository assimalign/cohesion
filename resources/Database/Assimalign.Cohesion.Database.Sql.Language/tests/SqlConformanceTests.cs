using System;
using System.Linq;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Conformance corpora for the declared dialect (#171/#172/#174): every statement
/// in DIALECT.md's supported matrix parses without error diagnostics, malformed and
/// hostile inputs never throw, literals type as documented, and the type-name table
/// resolves exactly the declared names.
/// </summary>
public class SqlConformanceTests
{
    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);

    // ── The supported-statement corpus ─────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: supported statements parse without errors")]
    [InlineData("SELECT * FROM users;", SqlQueryCommandType.Select)]
    [InlineData("SELECT a FROM t UNION SELECT a FROM u;", SqlQueryCommandType.Select)]
    [InlineData("SELECT a FROM t INTERSECT ALL SELECT a FROM u;", SqlQueryCommandType.Select)]
    [InlineData("SELECT a FROM t EXCEPT DISTINCT SELECT a FROM u;", SqlQueryCommandType.Select)]
    [InlineData("(SELECT a FROM t ORDER BY a LIMIT 1) UNION ALL (SELECT a FROM u ORDER BY a LIMIT 1) ORDER BY 1;", SqlQueryCommandType.Select)]
    [InlineData("SELECT DISTINCT u.name AS n, COUNT(*) FROM dbo.users u INNER JOIN orders o ON u.id = o.user_id WHERE u.age >= 21 AND o.total > 100.50 GROUP BY u.name HAVING COUNT(*) > 5 ORDER BY n DESC LIMIT 10 OFFSET 20;", SqlQueryCommandType.Select)]
    [InlineData("SELECT CASE WHEN age >= 18 THEN 'adult' ELSE 'minor' END FROM people;", SqlQueryCommandType.Select)]
    [InlineData("SELECT CAST(price AS DECIMAL(10, 2)) FROM products;", SqlQueryCommandType.Select)]
    [InlineData("SELECT name FROM t WHERE note LIKE 'a%' AND x NOT BETWEEN 1 AND 9 AND y IS NOT NULL;", SqlQueryCommandType.Select)]
    [InlineData("INSERT INTO users (id, name) VALUES (1, 'Ada'), (2, 'Grace');", SqlQueryCommandType.Insert)]
    [InlineData("INSERT INTO users (id, name) VALUES (1, DEFAULT), (DEFAULT, 'Grace');", SqlQueryCommandType.Insert)]
    [InlineData("INSERT INTO users DEFAULT VALUES;", SqlQueryCommandType.Insert)]
    [InlineData("INSERT INTO users (name) DEFAULT VALUES;", SqlQueryCommandType.Insert)]
    [InlineData("UPDATE dbo.users SET name = 'Ada', age = age + 1 WHERE id = @id;", SqlQueryCommandType.Update)]
    [InlineData("UPDATE dbo.users AS u SET age = age + 1 WHERE u.id BETWEEN 1 AND 10 RETURNING id, age;", SqlQueryCommandType.Update)]
    [InlineData("UPDATE dbo.users SET name = DEFAULT WHERE id = 1;", SqlQueryCommandType.Update)]
    [InlineData("DELETE FROM users WHERE id = $1;", SqlQueryCommandType.Delete)]
    [InlineData("DELETE FROM dbo.users AS u WHERE u.id = @id AND EXISTS (SELECT a.id FROM admins a WHERE a.id = u.id) RETURNING id;", SqlQueryCommandType.Delete)]
    [InlineData("DELETE FROM users;", SqlQueryCommandType.Delete)]
    [InlineData("CREATE TABLE IF NOT EXISTS users (id BIGINT PRIMARY KEY, name VARCHAR(100) NOT NULL, balance DECIMAL(18, 4) DEFAULT 0, created TIMESTAMP);", SqlQueryCommandType.Create)]
    [InlineData("CREATE TABLE national (a NATIONAL CHAR(10), b NATIONAL CHARACTER(10), c NCHAR(10), d NATIONAL CHAR VARYING(10), e NATIONAL CHARACTER VARYING(10), varying NCHAR VARYING(10));", SqlQueryCommandType.Create)]
    [InlineData("CREATE DOMAIN national AS NATIONAL CHAR(10);", SqlQueryCommandType.Create)]
    [InlineData("CREATE DOMAIN national AS NATIONAL CHAR VARYING(10);", SqlQueryCommandType.Create)]
    [InlineData("CREATE DOMAIN national AS NCHAR VARYING(10);", SqlQueryCommandType.Create)]
    [InlineData("SELECT CAST(varying AS NATIONAL CHAR(10)), CAST(varying AS NATIONAL CHAR VARYING(10)), CAST(varying AS NCHAR VARYING(10)) FROM national;", SqlQueryCommandType.Select)]
    [InlineData("SELECT INTERVAL '12', CAST('-12.5' AS INTERVAL), CAST('13:45' AS TIME);", SqlQueryCommandType.Select)]
    [InlineData("ALTER TABLE users ADD COLUMN email VARCHAR(255);", SqlQueryCommandType.Alter)]
    [InlineData("ALTER TABLE users DROP COLUMN email;", SqlQueryCommandType.Alter)]
    [InlineData("DROP TABLE IF EXISTS users;", SqlQueryCommandType.Drop)]
    [InlineData("CREATE INDEX ix_users_name ON users (name);", SqlQueryCommandType.Create)]
    [InlineData("CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email ON dbo.users (email, tenant_id);", SqlQueryCommandType.Create)]
    [InlineData("DROP INDEX IF EXISTS ix_users_name ON users;", SqlQueryCommandType.Drop)]
    [InlineData("select lower(NAME) from USERS where ID = 1;", SqlQueryCommandType.Select)] // case-insensitivity
    [InlineData("SELECT \"a\"\"b\" AS \"c\"\"d\" FROM \"t\"\"x\";", SqlQueryCommandType.Select)]
    [InlineData("SELECT /* block /* nested */ comment */ id FROM t; -- trailing", SqlQueryCommandType.Select)]
    public void Parse_SupportedCorpus_ShouldParseWithoutErrors(string sql, SqlQueryCommandType expected)
    {
        // Act
        var statement = Parse(sql);

        // Assert
        statement.SqlExpression.CommandType.ShouldBe(expected);
        statement.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        statement.SqlExpression.Text.ShouldBe(sql); // the raw text is stamped (#170)
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: executable subqueries parse without errors")]
    [InlineData("SELECT id FROM users WHERE id IN (SELECT user_id FROM admins);", SqlQueryCommandType.Select)]
    [InlineData("INSERT INTO archive SELECT * FROM users WHERE inactive = TRUE;", SqlQueryCommandType.Insert)]
    [InlineData("WITH a AS (SELECT id FROM users), b AS (SELECT id FROM a) SELECT * FROM b;", SqlQueryCommandType.Select)]
    [InlineData("WITH a AS (SELECT id FROM users) INSERT INTO archive SELECT id FROM a;", SqlQueryCommandType.Insert)]
    [InlineData("INSERT INTO archive WITH a AS (SELECT id FROM users) SELECT id FROM a;", SqlQueryCommandType.Insert)]
    [InlineData("WITH a AS (SELECT id FROM users) UPDATE users SET id = 3 WHERE id IN (SELECT id FROM a);", SqlQueryCommandType.Update)]
    [InlineData("WITH a AS (SELECT id FROM users) DELETE FROM users WHERE id IN (SELECT id FROM a);", SqlQueryCommandType.Delete)]
    public void Parse_ExecutableSubqueryCorpus_ShouldParseWithoutErrors(string sql, SqlQueryCommandType expected)
    {
        var statement = Parse(sql);

        statement.SqlExpression.CommandType.ShouldBe(expected);
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        statement.SqlExpression.Text.ShouldBe(sql);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: unexecuted statements report model diagnostics")]
    [InlineData("SELECT * FROM a LEFT OUTER JOIN b ON a.id = b.id FULL OUTER JOIN c ON b.id = c.id;", SqlQueryCommandType.Select)]
    public void Parse_UnexecutedCorpus_ShouldReportModelDiagnostic(string sql, SqlQueryCommandType expected)
    {
        // Outer joins retain explicit unsupported diagnostics (#1019).
        var statement = Parse(sql);

        statement.SqlExpression.CommandType.ShouldBe(expected);
        statement.Diagnostics.ShouldContain(d =>
            d.Code == "CK00001" && d.Severity == DiagnosticSeverity.Error);
        statement.SqlExpression.Text.ShouldBe(sql); // the raw text is stamped (#170)
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: temporal suffix lookahead leaves modifier validation to the grammar")]
    [InlineData("()", "CS00005")]
    [InlineData("(-1)", "CS00005")]
    [InlineData("(x)", "CS00005")]
    [InlineData("(1,2,3)", "CS00005")]
    [InlineData("(x, (1))", "CS00005")]
    [InlineData("( /*arg*/ x)", "CS00005")]
    [InlineData("(0)", "CK00001")]
    [InlineData("(6)", "CK00001")]
    [InlineData("(7)", "CK00001")]
    public void Parse_TemporalModifierBeforeSuffix_ShouldReportOnlyModifierDiagnostic(string modifier, string code)
    {
        // Arrange
        var parser = new SqlQueryParser();
        foreach (string suffix in new[] { "", " WITH TIME ZONE" })
        {
            foreach (var (sql, subject) in SqlTypeModifierCases.Statements("TIMESTAMP" + modifier + suffix))
            {
                // Act
                var diagnostics = parser.Parse(sql + ";").Diagnostics;

                // Assert
                var diagnostic = diagnostics.ShouldHaveSingleItem(sql);
                diagnostic.Code.ShouldBe(code, sql);
                diagnostic.Message.ShouldNotBeNull().ShouldContain(subject, Case.Sensitive);
            }
        }
    }

    // ── The malformed / hostile corpus ─────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: malformed inputs never throw")]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    [InlineData(";")]
    [InlineData("SELECT")]
    [InlineData("SELECT FROM WHERE;")]
    [InlineData("SELECT * FROM;")]
    [InlineData("INSERT INTO;")]
    [InlineData("INSERT users VALUES;")]
    [InlineData("UPDATE SET;")]
    [InlineData("DELETE;")]
    [InlineData("CREATE TABLE;")]
    [InlineData("CREATE TABLE t (;")]
    [InlineData("CREATE INDEX;")]
    [InlineData("CREATE UNIQUE INDEX ix ON;")]
    [InlineData("DROP INDEX;")]
    [InlineData("ALTER TABLE;")]
    [InlineData("DROP;")]
    [InlineData("SELECT ((((((1;")]
    [InlineData("SELECT 'unterminated string FROM t;")]
    [InlineData("SELECT * FROM t WHERE x = ;")]
    [InlineData("SELECT CASE WHEN THEN END FROM t;")]
    [InlineData("SELECT CAST(x AS) FROM t;")]
    [InlineData("MERGE INTO t USING s ON 1=1;")]
    [InlineData("TRUNCATE TABLE t;")]
    [InlineData("GRANT ALL ON t TO role;")]
    [InlineData("🙃 emoji soup ¯\\_(ツ)_/¯;")]
    [InlineData("SELECT \0 FROM t;")]
    public void Parse_MalformedCorpus_ShouldNeverThrow(string sql)
    {
        // Act: total behavior — a statement always comes back, never an exception.
        var statement = Should.NotThrow(() => Parse(sql));

        // Assert
        statement.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Conformance: unknown commands carry CS00002, empty input CS00001")]
    public void Parse_UnknownAndEmpty_ShouldCarryDeclaredDiagnostics()
    {
        Parse("MERGE INTO t;").Diagnostics.ShouldContain(d => d.Code == "CS00002");
        Parse("").Diagnostics.ShouldContain(d => d.Code == "CS00001");
        Parse("SELECT 1").Diagnostics.ShouldContain(d => d.Code == "CS00100");
        Parse("SELECT 1;").Diagnostics.ShouldNotContain(d => d.Code == "CS00100");
    }

    // ── Literal handling (#174) ────────────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Literals: forms type as documented")]
    [InlineData("SELECT 42;", SqlLiteralType.Integer, "42")]
    [InlineData("SELECT 3.14;", SqlLiteralType.Float, "3.14")]
    [InlineData("SELECT .5;", SqlLiteralType.Float, ".5")]
    [InlineData("SELECT 1e10;", SqlLiteralType.Float, "1e10")]
    [InlineData("SELECT 'text';", SqlLiteralType.String, "text")]
    [InlineData("SELECT TRUE;", SqlLiteralType.Boolean, "TRUE")]
    [InlineData("SELECT NULL;", SqlLiteralType.Null, "NULL")]
    public void Parse_LiteralForms_ShouldTypeAsDocumented(string sql, SqlLiteralType expectedType, string expectedValue)
    {
        // Act
        var statement = Parse(sql);
        var select = (SqlSelectExpression)statement.SqlExpression;
        var literal = select.Columns[0].Expression.ShouldBeOfType<SqlLiteralExpression>();

        // Assert
        literal.LiteralType.ShouldBe(expectedType);
        literal.Value.ShouldBe(expectedValue, StringCompareShould.IgnoreCase);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Literals: doubled quotes unescape inside strings")]
    public void Parse_EscapedStringLiteral_ShouldUnescape()
    {
        var statement = Parse("SELECT 'it''s';");
        var select = (SqlSelectExpression)statement.SqlExpression;
        var literal = select.Columns[0].Expression.ShouldBeOfType<SqlLiteralExpression>();
        literal.Value.ShouldContain("it", Case.Sensitive);
    }

    // ── Type-name resolution (#173) ────────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Types: declared names resolve to shared identities")]
    [InlineData("BOOLEAN", DatabaseType.Boolean)]
    [InlineData("bool", DatabaseType.Boolean)]
    [InlineData("TINYINT", DatabaseType.Int8)]
    [InlineData("SMALLINT", DatabaseType.Int16)]
    [InlineData("INT", DatabaseType.Int32)]
    [InlineData("integer", DatabaseType.Int32)]
    [InlineData("BIGINT", DatabaseType.Int64)]
    [InlineData("REAL", DatabaseType.Float32)]
    [InlineData("FLOAT", DatabaseType.Float64)]
    [InlineData("DOUBLE", DatabaseType.Float64)]
    [InlineData("DECIMAL", DatabaseType.Decimal)]
    [InlineData("NUMERIC", DatabaseType.Decimal)]
    [InlineData("VARCHAR", DatabaseType.String)]
    [InlineData("TEXT", DatabaseType.String)]
    [InlineData("NATIONAL CHAR", DatabaseType.Character)]
    [InlineData("NATIONAL CHARACTER", DatabaseType.Character)]
    [InlineData("NCHAR", DatabaseType.Character)]
    [InlineData("NATIONAL CHAR VARYING", DatabaseType.String)]
    [InlineData("NATIONAL CHARACTER VARYING", DatabaseType.String)]
    [InlineData("NCHAR VARYING", DatabaseType.String)]
    [InlineData("BYTEA", DatabaseType.Binary)]
    [InlineData("DATE", DatabaseType.Date)]
    [InlineData("TIME", DatabaseType.Time)]
    [InlineData("TIMESTAMP", DatabaseType.DateTime)]
    [InlineData("TIMESTAMPTZ", DatabaseType.DateTimeOffset)]
    [InlineData("INTERVAL", DatabaseType.TimeSpan)]
    [InlineData("UUID", DatabaseType.Guid)]
    [InlineData("JSON", DatabaseType.Json)]
    [InlineData("JSONB", DatabaseType.JsonBinary)]
    public void TryResolve_DeclaredNames_ShouldResolve(string name, DatabaseType expected)
    {
        SqlTypeNames.TryResolve(name, out var type).ShouldBeTrue();
        type.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Types: arguments map to length vs precision correctly")]
    public void TryResolve_TypeArguments_ShouldMapConstraints()
    {
        SqlTypeNames.TryResolve("VARCHAR", 100, null, null, out var varchar).ShouldBeTrue();
        varchar.MaxLength.ShouldBe(100);
        varchar.Precision.ShouldBeNull();

        // DECIMAL(18, 4): first argument is precision, not length.
        SqlTypeNames.TryResolve("DECIMAL", 18, null, 4, out var dec).ShouldBeTrue();
        dec.Precision.ShouldBe(18);
        dec.Scale.ShouldBe(4);
        dec.MaxLength.ShouldBeNull();

        SqlTypeNames.TryResolve("MADEUPTYPE", out _).ShouldBeFalse();
    }

    // ── Builtin function recognition (#173) ────────────────────────────

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Builtins: executable functions parse and unsupported names report CK00001")]
    [InlineData("COUNT", "SELECT COUNT(*) FROM t;")]
    [InlineData("SUM", "SELECT SUM(x) FROM t;")]
    [InlineData("COALESCE", "SELECT COALESCE(a, b, 0) FROM t;")]
    [InlineData("UPPER", "SELECT UPPER(name) FROM t;")]
    [InlineData("SUBSTRING", "SELECT SUBSTRING(name, 1, 3) FROM t;")]
    [InlineData("ABS", "SELECT ABS(-5) FROM t;")]
    [InlineData("ROUND", "SELECT ROUND(price, 2) FROM t;")]
    [InlineData("NOW", "SELECT NOW() FROM t;")]
    public void Parse_DeclaredBuiltins_ShouldParseAsFunctionCalls(string function, string sql)
    {
        var statement = Parse(sql);
        if (!SqlLanguageProfile.Instance.Functions.Contains(function, StringComparer.OrdinalIgnoreCase))
        {
            var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CK00001");
            diagnostic.Message.ShouldNotBeNull().ShouldContain(function, Case.Sensitive);
            return;
        }
        var select = (SqlSelectExpression)statement.SqlExpression;
        var call = select.Columns[0].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
        call.FunctionName.ShouldBe(function, StringCompareShould.IgnoreCase);
    }
}
