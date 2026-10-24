using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlDefaultParserTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: mixed VALUES items retain node kind and source span")]
    public void Parse_DefaultItems_ShouldRetainNodesAndSpans()
    {
        // Arrange
        const string sql = "INSERT INTO t (a, b) VALUES (1, default), (DEFAULT, NULL);";

        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();
        insert.IsDefaultValues.ShouldBeFalse();
        insert.Values!.Count.ShouldBe(2);
        var item = insert.Values[0][1].ShouldBeOfType<SqlDefaultExpression>();
        item.Location.ShouldNotBeNull();
        sql[item.Location.Start..item.Location.End].ShouldBe("default");
        SqlExpressionRenderer.Render(item).ShouldBe("DEFAULT");
        insert.Values[1][0].ShouldBeOfType<SqlDefaultExpression>();
        insert.Values[1][1].ShouldBeOfType<SqlLiteralExpression>().LiteralType.ShouldBe(SqlLiteralType.Null);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT VALUES: one omitted row is distinct from value lists and SELECT")]
    [InlineData("INSERT INTO t DEFAULT VALUES;")]
    [InlineData("INSERT INTO t (b, a) default /* trivia */ values;")]
    public void Parse_DefaultValues_ShouldRetainFlag(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();
        insert.IsDefaultValues.ShouldBeTrue();
        insert.Values.ShouldBeNull();
        insert.SelectSource.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - UPDATE DEFAULT: SET uses the same contextually typed node")]
    public void Parse_UpdateDefault_ShouldUseDefaultNode()
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("UPDATE t SET a = DEFAULT, b = 2 WHERE a = 1;");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlUpdateExpression>().Assignments[0].Value.ShouldBeOfType<SqlDefaultExpression>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: operands and incomplete forms are syntax errors")]
    [InlineData("INSERT INTO t VALUES (DEFAULT + 1);")]
    [InlineData("INSERT INTO t VALUES (1 + DEFAULT);")]
    [InlineData("INSERT INTO t VALUES ((DEFAULT));")]
    [InlineData("INSERT INTO t VALUES (COALESCE(DEFAULT, 1));")]
    [InlineData("INSERT INTO t VALUES (CAST(DEFAULT AS INT));")]
    [InlineData("INSERT INTO t VALUES (CASE WHEN TRUE THEN DEFAULT ELSE 1 END);")]
    [InlineData("INSERT INTO t VALUES (-DEFAULT);")]
    [InlineData("INSERT INTO t VALUES (DEFAULT COLLATE binary);")]
    [InlineData("INSERT INTO t DEFAULT;")]
    [InlineData("INSERT INTO t DEFAULT VALUES (1);")]
    [InlineData("INSERT INTO t DEFAULT VALUES, (1);")]
    [InlineData("UPDATE t SET a = DEFAULT + 1;")]
    [InlineData("UPDATE t SET a = COALESCE(DEFAULT, 1);")]
    [InlineData("UPDATE t SET a = 1 + DEFAULT;")]
    public void Parse_InvalidDefault_ShouldReportSyntaxError(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: bare scalar uses report a located syntax error")]
    [InlineData("SELECT DEFAULT FROM t;")]
    [InlineData("SELECT c FROM t WHERE DEFAULT = 42;")]
    [InlineData("SELECT COALESCE(DEFAULT, 0) FROM t;")]
    [InlineData("SELECT 1 + DEFAULT FROM t;")]
    [InlineData("SELECT -DEFAULT FROM t;")]
    [InlineData("INSERT INTO t (c) SELECT DEFAULT FROM t;")]
    [InlineData("SELECT c FROM t ORDER BY DEFAULT;")]
    [InlineData("CREATE TABLE t (c INT DEFAULT DEFAULT);")]
    public void Parse_ScalarDefault_ShouldReportKeywordSpan(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CS00003");
        diagnostic.Message.ShouldBe("DEFAULT is not allowed in this context.");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe("DEFAULT");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: qualified field names parse as columns in SET and VALUES expressions")]
    [InlineData("UPDATE t SET c = t.default + 1;")]
    [InlineData("UPDATE t SET c = t.\"default\" + 1;")]
    [InlineData("INSERT INTO t VALUES (t.default + 1);")]
    [InlineData("INSERT INTO t VALUES (t.\"default\" + 1);")]
    public void Parse_QualifiedDefaultColumn_ShouldPreserveColumnExpression(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        SqlExpression expression = statement.SqlExpression is SqlUpdateExpression update
            ? update.Assignments[0].Value : ((SqlInsertExpression)statement.SqlExpression).Values![0][0];
        SqlExpressionRenderer.Render(expression).ShouldBe("t.\"default\" + 1");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: quoted words, strings and comments remain ordinary expression contents")]
    [InlineData("INSERT INTO t VALUES ('DEFAULT', 1 /* DEFAULT */);")]
    [InlineData("UPDATE t SET a = \"DEFAULT\" + 1;")]
    [InlineData("SELECT \"DEFAULT\" FROM t;")]
    [InlineData("UPDATE t SET a = 1 /* DEFAULT */ + 2;")]
    public void Parse_DefaultTextInOtherTokens_ShouldPreserveExpression(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }
}
