using FluentAssertions;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Sql.Tests;

public class SqlSelectParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_SelectStar_ReturnsSelectExpressionWithStarColumn()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT * FROM Users;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.CommandType.Should().Be(SqlQueryCommandType.Select);
        select.Columns.Should().HaveCount(1);
        select.Columns[0].Expression.Should().BeOfType<SqlStarExpression>();
        select.Columns[0].Alias.Should().BeNull();
        select.From.Should().NotBeNull();
        select.From!.TableName.Should().Be("Users");
    }

    [Fact]
    public void Parse_SelectColumns_ParsesMultipleColumns()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT Id, Name FROM Users;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Columns.Should().HaveCount(2);
        var col0 = select.Columns[0].Expression.Should().BeOfType<SqlColumnReferenceExpression>().Subject;
        col0.ColumnName.Should().Be("Id");
        var col1 = select.Columns[1].Expression.Should().BeOfType<SqlColumnReferenceExpression>().Subject;
        col1.ColumnName.Should().Be("Name");
    }

    [Fact]
    public void Parse_SelectWithAlias_ParsesColumnAlias()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT Name AS UserName FROM Users;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Columns.Should().HaveCount(1);
        select.Columns[0].Alias.Should().Be("UserName");
    }

    [Fact]
    public void Parse_SelectFromSchemaQualifiedTable_ParsesSchemaAndTable()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT * FROM dbo.Users;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.From.Should().NotBeNull();
        select.From!.SchemaName.Should().Be("dbo");
        select.From.TableName.Should().Be("Users");
    }

    [Fact]
    public void Parse_SelectWithTableAlias_ParsesAlias()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT u.Id FROM Users u;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.From.Should().NotBeNull();
        select.From!.TableName.Should().Be("Users");
        select.From.Alias.Should().Be("u");
    }

    [Fact]
    public void Parse_SelectWithWhere_ParsesWhereClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT * FROM Users WHERE Id = 1;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Where.Should().NotBeNull();
        var binary = select.Where.Should().BeOfType<SqlBinaryExpression>().Subject;
        binary.Operator.Should().Be(SqlBinaryOperator.Equal);

        var left = binary.Left.Should().BeOfType<SqlColumnReferenceExpression>().Subject;
        left.ColumnName.Should().Be("Id");

        var right = binary.Right.Should().BeOfType<SqlLiteralExpression>().Subject;
        right.Value.Should().Be("1");
        right.LiteralType.Should().Be(SqlLiteralType.Integer);
    }

    [Fact]
    public void Parse_SelectDistinct_SetsIsDistinct()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT DISTINCT Name FROM Users;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.IsDistinct.Should().BeTrue();
        select.Columns.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_SelectWithInnerJoin_ParsesJoinClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT u.Id FROM Users u INNER JOIN Orders o ON u.Id = o.UserId;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Joins.Should().HaveCount(1);
        select.Joins[0].JoinType.Should().Be(SqlJoinType.Inner);
        select.Joins[0].Table.TableName.Should().Be("Orders");
        select.Joins[0].Table.Alias.Should().Be("o");
        select.Joins[0].Condition.Should().NotBeNull();
    }

    [Fact]
    public void Parse_SelectWithLeftJoin_ParsesLeftOuterJoin()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users u LEFT JOIN Orders o ON u.Id = o.UserId;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Joins.Should().HaveCount(1);
        select.Joins[0].JoinType.Should().Be(SqlJoinType.LeftOuter);
    }

    [Fact]
    public void Parse_SelectWithGroupBy_ParsesGroupByClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT Status FROM Users GROUP BY Status;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.GroupBy.Should().HaveCount(1);
        var col = select.GroupBy[0].Should().BeOfType<SqlColumnReferenceExpression>().Subject;
        col.ColumnName.Should().Be("Status");
    }

    [Fact]
    public void Parse_SelectWithHaving_ParsesHavingClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT Status FROM Users GROUP BY Status HAVING COUNT(*) > 5;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Having.Should().NotBeNull();
        var binary = select.Having.Should().BeOfType<SqlBinaryExpression>().Subject;
        binary.Operator.Should().Be(SqlBinaryOperator.GreaterThan);
    }

    [Fact]
    public void Parse_SelectWithOrderBy_ParsesOrderByClause()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users ORDER BY Name DESC;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.OrderBy.Should().HaveCount(1);
        select.OrderBy[0].IsDescending.Should().BeTrue();
        var col = select.OrderBy[0].Expression.Should().BeOfType<SqlColumnReferenceExpression>().Subject;
        col.ColumnName.Should().Be("Name");
    }

    [Fact]
    public void Parse_SelectWithLimitOffset_ParsesLimitAndOffset()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users LIMIT 10 OFFSET 20;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Limit.Should().NotBeNull();
        var limit = select.Limit.Should().BeOfType<SqlLiteralExpression>().Subject;
        limit.Value.Should().Be("10");

        select.Offset.Should().NotBeNull();
        var offset = select.Offset.Should().BeOfType<SqlLiteralExpression>().Subject;
        offset.Value.Should().Be("20");
    }

    [Fact]
    public void Parse_SelectWithSubqueryInWhere_ParsesSubquery()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "SELECT * FROM Users WHERE Id IN (SELECT UserId FROM Orders);");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Where.Should().BeOfType<SqlInExpression>();
        var inExpr = (SqlInExpression)select.Where!;
        inExpr.Subquery.Should().NotBeNull();
        inExpr.Values.Should().BeNull();
    }

    [Fact]
    public void Parse_SelectCountStar_ParsesFunctionCall()
    {
        var statement = (SqlQueryStatement)_parser.Parse("SELECT COUNT(*) FROM Users;");
        var select = statement.SqlExpression.Should().BeOfType<SqlSelectExpression>().Subject;

        select.Columns.Should().HaveCount(1);
        var func = select.Columns[0].Expression.Should().BeOfType<SqlFunctionCallExpression>().Subject;
        func.FunctionName.Should().Be("COUNT");
        func.Arguments.Should().HaveCount(1);
        func.Arguments[0].Should().BeOfType<SqlStarExpression>();
    }
}
