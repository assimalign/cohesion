using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlT1FunctionGrammarTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - T1 functions: standard forms normalize to calls and render canonically")]
    [InlineData("SUBSTRING(s FROM a)", "SUBSTRING", 2)]
    [InlineData("SUBSTRING(s FROM a FOR b)", "SUBSTRING", 3)]
    [InlineData("SUBSTRING(s, a, b)", "SUBSTRING", 3)]
    [InlineData("TRIM(s)", "TRIM", 1)]
    [InlineData("TRIM(\"leading\")", "TRIM", 1)]
    [InlineData("TRIM(\"trailing\")", "TRIM", 1)]
    [InlineData("TRIM(\"both\")", "TRIM", 1)]
    [InlineData("TRIM(LEADING FROM s)", "LTRIM", 1)]
    [InlineData("TRIM(TRAILING '😀' FROM s)", "RTRIM", 2)]
    [InlineData("TRIM(BOTH chars FROM s)", "TRIM", 2)]
    [InlineData("TRIM(chars FROM s)", "TRIM", 2)]
    [InlineData("POSITION(sub IN s)", "POSITION", 2)]
    [InlineData("CURRENT_DATE", "CURRENT_DATE", 0)]
    [InlineData("current_date", "current_date", 0)]
    [InlineData("CURRENT_TIME", "CURRENT_TIME", 0)]
    [InlineData("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP", 0)]
    [InlineData("LOCALTIME", "LOCALTIME", 0)]
    [InlineData("LOCALTIMESTAMP", "LOCALTIMESTAMP", 0)]
    [InlineData("localtimestamp", "localtimestamp", 0)]
    public void Parse_StandardForm_ShouldNormalizeAndRoundTrip(string expression, string name, int count)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {expression} FROM t;");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var call = ((SqlSelectExpression)statement.SqlExpression).Columns[0].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
        call.FunctionName.ShouldBe(name);
        call.Arguments.Count.ShouldBe(count);
        var canonical = SqlExpressionRenderer.Render(call);
        var rendered = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {canonical} FROM t;");
        rendered.Diagnostics.ShouldBeEmpty();
        SqlExpressionRenderer.Render(((SqlSelectExpression)rendered.SqlExpression).Columns[0].Expression).ShouldBe(canonical);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - T1 functions: malformed standard forms report SQL0003")]
    [InlineData("SUBSTRING(s FOR 2)")]
    [InlineData("SUBSTRING(s 2)")]
    [InlineData("POSITION(s, t)")]
    [InlineData("EXTRACT(YEAR ts)")]
    [InlineData("EXTRACT(FORTNIGHT FROM ts)")]
    [InlineData("TRIM(LEADING chars s)")]
    [InlineData("CURRENT_DATE()")]
    [InlineData("CURRENT_TIME()")]
    [InlineData("CURRENT_TIMESTAMP()")]
    [InlineData("LOCALTIME()")]
    [InlineData("LOCALTIMESTAMP()")]
    [InlineData("CURRENT_TIME(3, 4)")]
    [InlineData("UPPER(name")]
    public void Parse_MalformedStandardForm_ShouldReject(string expression)
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse($"SELECT {expression} FROM t;");

        // Assert
        parsed.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - T1 functions: precision arguments retain a reusable capability rejection")]
    [InlineData("CURRENT_TIME(3)")]
    [InlineData("CURRENT_TIMESTAMP(3)")]
    [InlineData("LOCALTIME(0)")]
    [InlineData("LOCALTIMESTAMP(6)")]
    public void Parse_PrecisionArgument_ShouldNameUnsupportedPrecision(string expression)
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse($"SELECT {expression} FROM t;");

        // Assert
        var diagnostic = parsed.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain("precision argument", Case.Sensitive);
        var declaration = new SqlQueryParser().Parse($"CREATE TABLE t (ts TIMESTAMP DEFAULT {expression});");
        declaration.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBL001");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - T1 functions: every EXTRACT field retains grammar and identifiers retain their roles")]
    public void Parse_FieldsAndIdentifiers_ShouldPreserveContextualNames()
    {
        // Arrange / Act / Assert
        foreach (var field in new[] { "YEAR", "QUARTER", "MONTH", "WEEK", "DAY", "DOW", "DOY", "HOUR", "MINUTE", "SECOND", "EPOCH" })
        {
            var parsed = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT EXTRACT({field} FROM ts) FROM t;");
            parsed.Diagnostics.ShouldBeEmpty();
            var call = ((SqlSelectExpression)parsed.SqlExpression).Columns[0].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
            call.FunctionName.ShouldBe("EXTRACT");
            call.Arguments[0].ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe(field);
            new SqlQueryParser().Parse($"SELECT {SqlExpressionRenderer.Render(call)} FROM t;").Diagnostics.ShouldBeEmpty();
        }
        foreach (string name in new[] { "position", "sign", "reverse", "ln", "exp", "now", "interval", "left", "current_date", "localtime", "localtimestamp" })
        {
            new SqlQueryParser().Parse($"CREATE TABLE {name} ({name} INT);").Diagnostics.ShouldBeEmpty();
            // LEFT remains a clause keyword; its target-list spelling already required quotes.
            if (name == "left")
            {
                new SqlQueryParser().Parse("INSERT INTO t (left) VALUES (1);").Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("SQL0003");
            }
            else
            {
                new SqlQueryParser().Parse($"INSERT INTO t ({name}) VALUES (1);").Diagnostics.ShouldBeEmpty(name);
                new SqlQueryParser().Parse($"UPDATE t SET {name} = 1;").Diagnostics.ShouldBeEmpty(name);
                new SqlQueryParser().Parse($"CREATE INDEX ix ON t ({name});").Diagnostics.ShouldBeEmpty(name);
            }
            var reference = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {name} FROM t;");
            reference.Diagnostics.ShouldBeEmpty();
            if (name is "current_date" or "localtime" or "localtimestamp")
            {
                ((SqlSelectExpression)reference.SqlExpression).Columns[0].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
            }
            else
            {
                ((SqlSelectExpression)reference.SqlExpression).Columns[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>().ColumnName.ShouldBe(name);
            }
            new SqlQueryParser().Parse($"SELECT \"{name}\" AS {name} FROM t AS \"{name}\";").Diagnostics.ShouldBeEmpty(name);
            if (name is "current_date" or "localtime" or "localtimestamp") { continue; }
            var parsed = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {name}.id FROM t AS \"{name}\";");
            parsed.Diagnostics.ShouldBeEmpty();
            ((SqlSelectExpression)parsed.SqlExpression).Columns[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>().TableAlias.ShouldBe(name);
        }
        new SqlQueryParser().Parse("SELECT LEFT(s, 1), COUNT(*) FROM t GROUP BY LEFT /* trivia */ (s, 1) HAVING RIGHT(LEFT(s, 1), 1) = 'a' ORDER BY LEFT(s, 1);").Diagnostics.ShouldBeEmpty();
    }
}
