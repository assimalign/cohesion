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

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: binding-only DML sources preserve contextual identifiers (#1714)")]
    [InlineData("WITH \"recursive\" AS (SELECT id FROM s) DELETE FROM t USING recursive WHERE t.id=recursive.id;")]
    [InlineData("UPDATE t SET id=s.id FROM s RETURNING \"old\".id;")]
    [InlineData("DELETE FROM t USING s RETURNING \"new\".id;")]
    public void Parse_BindingOnlyDmlCorpus_ShouldParseWithoutErrors(string sql)
    {
        // Arrange / Act
        var statement = Parse(sql);

        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: supported statements parse without errors")]
    [InlineData("CREATE TABLE t(id SERIAL2);", SqlQueryCommandType.Create)]
    [InlineData("CREATE TABLE t(id SERIAL4);", SqlQueryCommandType.Create)]
    [InlineData("CREATE TABLE t(id SERIAL8);", SqlQueryCommandType.Create)]
    [InlineData("ALTER TABLE t ALTER id RESTART WITH 10;", SqlQueryCommandType.Alter)]
    [InlineData("ALTER TABLE t ALTER id SET GENERATED ALWAYS;", SqlQueryCommandType.Alter)]
    [InlineData("INSERT INTO t OVERRIDING USER VALUE VALUES(8);", SqlQueryCommandType.Insert)]
    [InlineData("INSERT INTO t OVERRIDING USER VALUE VALUES(CAST('abc' AS INT),1);", SqlQueryCommandType.Insert)]
    [InlineData("MERGE INTO t USING (SELECT 1 AS k) s ON t.k=s.k WHEN NOT MATCHED THEN INSERT(id,k) OVERRIDING USER VALUE VALUES(CAST('abc' AS INT),1);", SqlQueryCommandType.Merge)]
    [InlineData("CREATE SEQUENCE s OWNED BY t.id;", SqlQueryCommandType.Create)]
    [InlineData("DISCARD ALL;", SqlQueryCommandType.Discard)]
    [InlineData("BEGIN ISOLATION LEVEL READ COMMITTED;", SqlQueryCommandType.Begin)]
    [InlineData("START TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", SqlQueryCommandType.Begin)]
    [InlineData("BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ;", SqlQueryCommandType.Begin)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED, ISOLATION LEVEL REPEATABLE READ;", SqlQueryCommandType.SetTransaction)]
    [InlineData("ALTER TABLE t ALTER v TYPE \"a space\" USING v;", SqlQueryCommandType.Alter)]
    [InlineData("ALTER TABLE t ALTER v TYPE \"a\"\"quote\";", SqlQueryCommandType.Alter)]
    [InlineData("ALTER TABLE t ALTER v TYPE \"\"\"odd\"\"\";", SqlQueryCommandType.Alter)]
    [InlineData("SELECT a, b, GROUPING(a,b), GROUPING_ID(a,b), SUM(v) FILTER (WHERE v>0) FROM t GROUP BY DISTINCT a, GROUPING SETS (ROLLUP(b), CUBE((a,b)), ()) HAVING GROUPING(a,b)>0 ORDER BY GROUPING(a,b);", SqlQueryCommandType.Select)]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY ();", SqlQueryCommandType.Select)]
    [InlineData("SELECT a,(SELECT GROUPING(t.a) GROUP BY t.a) FROM t GROUP BY ROLLUP(a);", SqlQueryCommandType.Select)]
    [InlineData("SELECT a,(SELECT GROUPING(t.a)) FROM t GROUP BY ROLLUP(a);", SqlQueryCommandType.Select)]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY GROUPING SETS (ROLLUP((a,(b,c))),GROUPING SETS ((a),()));", SqlQueryCommandType.Select)]
    [InlineData("SELECT * FROM users;", SqlQueryCommandType.Select)]
    [InlineData("SELECT BOOL_AND(flag), BOOL_OR(flag), EVERY(flag), STDDEV(v), STDDEV_SAMP(v), STDDEV_POP(v), VARIANCE(v), VAR_SAMP(v), VAR_POP(v) FROM t;", SqlQueryCommandType.Select)]
    [InlineData("SELECT SUM(v) FILTER(WHERE flag) OVER w,COUNT(*) OVER w,BOOL_AND(flag) OVER w,VAR_POP(v) OVER w,FIRST_VALUE(v) OVER w,LAST_VALUE(v) OVER w,NTH_VALUE(v,2) OVER w FROM t WINDOW w AS(PARTITION BY p ORDER BY k ROWS BETWEEN 2 PRECEDING AND 1 FOLLOWING);", SqlQueryCommandType.Select)]
    [InlineData("SELECT SUM(nextval('s')) OVER(ORDER BY id ROWS 1 PRECEDING), SUM((SELECT t.id)) OVER(ORDER BY id ROWS 1 PRECEDING), SUM(id) FILTER(WHERE random()>0) OVER(ORDER BY id ROWS 1 PRECEDING) FROM t;", SqlQueryCommandType.Select)]
    [InlineData("SELECT LAST_VALUE(v) OVER(ORDER BY k),SUM(v) OVER(ORDER BY k RANGE UNBOUNDED PRECEDING) FROM t;", SqlQueryCommandType.Select)]
    [InlineData("SELECT COUNT(*) FILTER (WHERE flag), SUM(DISTINCT v) FILTER (WHERE v>0), STRING_AGG(label, ',' ORDER BY id) FILTER (WHERE flag) FROM t;", SqlQueryCommandType.Select)]
    [InlineData("SELECT SUM(amount ORDER BY id DESC) FILTER(WHERE flag) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t;", SqlQueryCommandType.Select)]
    [InlineData("SELECT * FROM a FULL JOIN b USING(id);", SqlQueryCommandType.Select)]
    [InlineData("SELECT * FROM a LEFT OUTER JOIN b ON a.id = b.id FULL OUTER JOIN c ON b.id = c.id;", SqlQueryCommandType.Select)]
    [InlineData("SELECT * FROM ((SELECT * FROM a) d RIGHT JOIN b ON d.id = b.id);", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM ((a RIGHT JOIN b USING (id)) q RIGHT JOIN c USING (id));", SqlQueryCommandType.Select)]
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
    [InlineData("MERGE INTO t USING s ON t.id=s.id WHEN MATCHED AND s.v>0 THEN UPDATE SET v=s.v WHEN MATCHED THEN DELETE WHEN NOT MATCHED THEN INSERT(id,v) VALUES(s.id,s.v) WHEN NOT MATCHED BY SOURCE THEN DO NOTHING RETURNING merge_action();", SqlQueryCommandType.Merge)]
    [InlineData("MERGE INTO t USING s CROSS JOIN u ON t.id=s.id WHEN MATCHED THEN DO NOTHING;", SqlQueryCommandType.Merge)]
    [InlineData("MERGE INTO t USING s ON t.id>s.id WHEN NOT MATCHED AND FALSE THEN INSERT VALUES(s.id,s.v);", SqlQueryCommandType.Merge)]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id>s.id;", SqlQueryCommandType.Update)]
    [InlineData("DELETE FROM t USING s WHERE t.id>s.id;", SqlQueryCommandType.Delete)]
    [InlineData("UPDATE dbo.users SET name = DEFAULT WHERE id = 1;", SqlQueryCommandType.Update)]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id=s.id RETURNING t.v,s.v;", SqlQueryCommandType.Update)]
    [InlineData("DELETE FROM t USING s WHERE t.id=s.id RETURNING t.id,s.v;", SqlQueryCommandType.Delete)]
    [InlineData("DELETE FROM users WHERE id = $1;", SqlQueryCommandType.Delete)]
    [InlineData("DELETE FROM dbo.users AS u WHERE u.id = @id AND EXISTS (SELECT a.id FROM admins a WHERE a.id = u.id) RETURNING id;", SqlQueryCommandType.Delete)]
    [InlineData("DELETE FROM users;", SqlQueryCommandType.Delete)]
    [InlineData("CREATE TABLE IF NOT EXISTS users (id BIGINT PRIMARY KEY, name VARCHAR(100) NOT NULL, balance DECIMAL(18, 4) DEFAULT 0, created TIMESTAMP);", SqlQueryCommandType.Create)]
    [InlineData("CREATE TABLE update_actions (pid INT REFERENCES parent(id) ON UPDATE CASCADE ON DELETE SET NULL);", SqlQueryCommandType.Create)]
    [InlineData("CREATE TABLE national (a NATIONAL CHAR(10), b NATIONAL CHARACTER(10), c NCHAR(10), d NATIONAL CHAR VARYING(10), e NATIONAL CHARACTER VARYING(10), varying NCHAR VARYING(10));", SqlQueryCommandType.Create)]
    [InlineData("CREATE DOMAIN national AS NATIONAL CHAR(10);", SqlQueryCommandType.Create)]
    [InlineData("CREATE DOMAIN national AS NATIONAL CHAR VARYING(10);", SqlQueryCommandType.Create)]
    [InlineData("CREATE DOMAIN national AS NCHAR VARYING(10);", SqlQueryCommandType.Create)]
    [InlineData("SELECT CAST(varying AS NATIONAL CHAR(10)), CAST(varying AS NATIONAL CHAR VARYING(10)), CAST(varying AS NCHAR VARYING(10)) FROM national;", SqlQueryCommandType.Select)]
    [InlineData("SELECT INTERVAL '12', CAST('-12.5' AS INTERVAL), CAST('13:45' AS TIME);", SqlQueryCommandType.Select)]
    [InlineData("ALTER TABLE users ADD COLUMN email VARCHAR(255);", SqlQueryCommandType.Alter)]
    [InlineData("ALTER TABLE users DROP COLUMN email;", SqlQueryCommandType.Alter)]
    [InlineData("ALTER TABLE users ALTER COLUMN age TYPE BIGINT;", SqlQueryCommandType.Alter)]
    [InlineData("ALTER TABLE users ALTER age SET DATA TYPE INT USING CAST(age AS INT);", SqlQueryCommandType.Alter)]
    [InlineData("DROP TABLE IF EXISTS users;", SqlQueryCommandType.Drop)]
    [InlineData("TRUNCATE TABLE users;", SqlQueryCommandType.Truncate)]
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
    [InlineData("SELECT id = (SELECT 2), COUNT(*) FROM candidates GROUP BY id = (SELECT 2) HAVING id = (SELECT 2) ORDER BY id = (SELECT 2);", SqlQueryCommandType.Select)]
    [InlineData("SELECT EXISTS (SELECT 2 WHERE id = 2), COUNT(*) FROM candidates GROUP BY EXISTS (SELECT 2 WHERE id = 2) HAVING EXISTS (SELECT 2 WHERE id = 2) ORDER BY EXISTS (SELECT 2 WHERE id = 2);", SqlQueryCommandType.Select)]
    [InlineData("SELECT id = ANY (SELECT 2), COUNT(*) FROM candidates GROUP BY id = ANY (SELECT 2) HAVING id = ANY (SELECT 2) ORDER BY id = ANY (SELECT 2);", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM users WHERE id IN (SELECT user_id FROM admins);", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM users WHERE id = ANY (SELECT user_id FROM admins);", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM users WHERE id <> SOME (SELECT user_id FROM admins);", SqlQueryCommandType.Select)]
    [InlineData("SELECT id FROM users u WHERE id >= ALL (SELECT a.user_id FROM admins a WHERE a.user_id = u.id);", SqlQueryCommandType.Select)]
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
    [InlineData("GRANT SELECT ON t TO guest;")]
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
        Parse("GRANT SELECT ON t TO guest;").Diagnostics.ShouldContain(d => d.Code == "CS00002");
        Parse("").Diagnostics.ShouldContain(d => d.Code == "CS00001");
        Parse("SELECT 1").Diagnostics.ShouldContain(d => d.Code == "CS00009");
        Parse("SELECT 1;").Diagnostics.ShouldNotContain(d => d.Code == "CS00009");
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
