using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public class SqlSelectParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_SelectStar_ReturnsSelectExpressionWithStarColumn()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT * FROM Users;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.CommandType.ShouldBe(SqlQueryCommandType.Select);
        select.Columns.Count.ShouldBe(1);
        select.Columns[0].Expression.ShouldBeOfType<SqlStarExpression>();
        select.Columns[0].Alias.ShouldBeNull();
        select.From.ShouldNotBeNull();
        select.From!.TableName.ShouldBe("Users");
    }

    [Fact]
    public void Parse_SelectColumns_ParsesMultipleColumns()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT Id, Name FROM Users;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Columns.Count.ShouldBe(2);
        var col0 = select.Columns[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>();
        col0.ColumnName.ShouldBe("Id");
        var col1 = select.Columns[1].Expression.ShouldBeOfType<SqlColumnReferenceExpression>();
        col1.ColumnName.ShouldBe("Name");
    }

    [Fact]
    public void Parse_SelectWithAlias_ParsesColumnAlias()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT Name AS UserName FROM Users;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Columns.Count.ShouldBe(1);
        select.Columns[0].Alias.ShouldBe("UserName");
    }

    [Fact]
    public void Parse_SelectFromSchemaQualifiedTable_ParsesSchemaAndTable()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT * FROM dbo.Users;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.From.ShouldNotBeNull();
        select.From!.SchemaName.ShouldBe("dbo");
        select.From.TableName.ShouldBe("Users");
    }

    [Fact]
    public void Parse_SelectWithTableAlias_ParsesAlias()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT u.Id FROM Users u;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.From.ShouldNotBeNull();
        select.From!.TableName.ShouldBe("Users");
        select.From.Alias.ShouldBe("u");
    }

    [Fact]
    public void Parse_SelectWithWhere_ParsesWhereClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT * FROM Users WHERE Id = 1;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Where.ShouldNotBeNull();
        var binary = select.Where.ShouldBeOfType<SqlBinaryExpression>();
        binary.Operator.ShouldBe(SqlBinaryOperator.Equal);

        var left = binary.Left.ShouldBeOfType<SqlColumnReferenceExpression>();
        left.ColumnName.ShouldBe("Id");

        var right = binary.Right.ShouldBeOfType<SqlLiteralExpression>();
        right.Value.ShouldBe("1");
        right.LiteralType.ShouldBe(SqlLiteralType.Integer);
    }

    [Fact]
    public void Parse_SelectDistinct_SetsIsDistinct()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT DISTINCT Name FROM Users;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.IsDistinct.ShouldBeTrue();
        select.Columns.Count.ShouldBe(1);
    }

    [Fact]
    public void Parse_SelectWithInnerJoin_ParsesJoinClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT u.Id FROM Users u INNER JOIN Orders o ON u.Id = o.UserId;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Joins.Count.ShouldBe(1);
        select.Joins[0].JoinType.ShouldBe(SqlJoinType.Inner);
        select.Joins[0].Table.TableName.ShouldBe("Orders");
        select.Joins[0].Table.Alias.ShouldBe("o");
        select.Joins[0].Condition.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_SelectWithLeftJoin_ParsesLeftOuterJoin()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users u LEFT JOIN Orders o ON u.Id = o.UserId;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Joins.Count.ShouldBe(1);
        select.Joins[0].JoinType.ShouldBe(SqlJoinType.LeftOuter);
    }

    [Fact]
    public void Parse_SelectWithGroupBy_ParsesGroupByClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT Status FROM Users GROUP BY Status;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.GroupBy.Count.ShouldBe(1);
        var col = select.GroupBy[0].ShouldBeOfType<SqlColumnReferenceExpression>();
        col.ColumnName.ShouldBe("Status");
    }

    [Fact]
    public void Parse_SelectWithHaving_ParsesHavingClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT Status FROM Users GROUP BY Status HAVING COUNT(*) > 5;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Having.ShouldNotBeNull();
        var binary = select.Having.ShouldBeOfType<SqlBinaryExpression>();
        binary.Operator.ShouldBe(SqlBinaryOperator.GreaterThan);
    }

    [Fact]
    public void Parse_SelectWithOrderBy_ParsesOrderByClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users ORDER BY Name DESC;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.OrderBy.Count.ShouldBe(1);
        select.OrderBy[0].IsDescending.ShouldBeTrue();
        var col = select.OrderBy[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>();
        col.ColumnName.ShouldBe("Name");
    }

    [Fact]
    public void Parse_SelectWithLimitOffset_ParsesLimitAndOffset()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users LIMIT 10 OFFSET 20;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Limit.ShouldNotBeNull();
        var limit = select.Limit.ShouldBeOfType<SqlLiteralExpression>();
        limit.Value.ShouldBe("10");

        select.Offset.ShouldNotBeNull();
        var offset = select.Offset.ShouldBeOfType<SqlLiteralExpression>();
        offset.Value.ShouldBe("20");
    }

    [Fact]
    public void Parse_SelectWithSubqueryInWhere_ParsesSubquery()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users WHERE Id IN (SELECT UserId FROM Orders);");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Where.ShouldBeOfType<SqlInExpression>();
        var inExpr = (SqlInExpression)select.Where!;
        inExpr.Subquery.ShouldNotBeNull();
        inExpr.Values.ShouldBeNull();
    }

    [Fact]
    public void Parse_SelectCountStar_ParsesFunctionCall()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT COUNT(*) FROM Users;");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        select.Columns.Count.ShouldBe(1);
        var func = select.Columns[0].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
        func.FunctionName.ShouldBe("COUNT");
        func.Arguments.Count.ShouldBe(1);
        func.Arguments[0].ShouldBeOfType<SqlStarExpression>();
    }
}
