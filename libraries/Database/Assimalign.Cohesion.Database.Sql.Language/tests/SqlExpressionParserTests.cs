using FluentAssertions;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Sql.Tests;

public class SqlExpressionParserTests
{
    private readonly SqlQueryParser _parser = new();

    // Helper: parse "SELECT <expr>;" and return the first column expression
    private SqlExpression ParseExpr(string expr)
    {
        var statement = (SqlQueryStatement)_parser.Parse($"SELECT {expr};");
        var select = (SqlSelectExpression)statement.SqlExpression;
        return (SqlExpression)select.Columns[0].Expression;
    }

    // Helper: parse "SELECT * FROM t WHERE <expr>;" and return the WHERE expression
    private SqlExpression ParseWhere(string whereExpr)
    {
        var statement = (SqlQueryStatement)_parser.Parse($"SELECT * FROM t WHERE {whereExpr};");
        var select = (SqlSelectExpression)statement.SqlExpression;
        return select.Where!;
    }

    [Fact]
    public void Parse_IntegerLiteral_ReturnsLiteralExpression()
    {
        var expr = ParseExpr("42");
        var literal = expr.Should().BeOfType<SqlLiteralExpression>().Subject;
        literal.Value.Should().Be("42");
        literal.LiteralType.Should().Be(SqlLiteralType.Integer);
    }

    [Fact]
    public void Parse_StringLiteral_ReturnsLiteralExpression()
    {
        var expr = ParseExpr("'hello'");
        var literal = expr.Should().BeOfType<SqlLiteralExpression>().Subject;
        literal.LiteralType.Should().Be(SqlLiteralType.String);
    }

    [Fact]
    public void Parse_FloatLiteral_ReturnsLiteralExpression()
    {
        var expr = ParseExpr("3.14");
        var literal = expr.Should().BeOfType<SqlLiteralExpression>().Subject;
        literal.Value.Should().Be("3.14");
        literal.LiteralType.Should().Be(SqlLiteralType.Float);
    }

    [Fact]
    public void Parse_NullLiteral_ReturnsNullLiteral()
    {
        var expr = ParseExpr("NULL");
        var literal = expr.Should().BeOfType<SqlLiteralExpression>().Subject;
        literal.LiteralType.Should().Be(SqlLiteralType.Null);
    }

    [Fact]
    public void Parse_BooleanLiteral_ReturnsBooleanLiteral()
    {
        var expr = ParseExpr("TRUE");
        var literal = expr.Should().BeOfType<SqlLiteralExpression>().Subject;
        literal.LiteralType.Should().Be(SqlLiteralType.Boolean);
        literal.Value.Should().Be("TRUE");
    }

    [Fact]
    public void Parse_Parameter_ReturnsParameterExpression()
    {
        var expr = ParseExpr("@userId");
        var param = expr.Should().BeOfType<SqlParameterExpression>().Subject;
        param.ParameterName.Should().Be("@userId");
    }

    [Fact]
    public void Parse_DollarParameter_ReturnsParameterExpression()
    {
        var expr = ParseExpr("$1");
        var param = expr.Should().BeOfType<SqlParameterExpression>().Subject;
        param.ParameterName.Should().Be("$1");
    }

    [Fact]
    public void Parse_ArithmeticPrecedence_MultiplicationBeforeAddition()
    {
        // 1 + 2 * 3 should parse as 1 + (2 * 3)
        var expr = ParseExpr("1 + 2 * 3");
        var add = expr.Should().BeOfType<SqlBinaryExpression>().Subject;
        add.Operator.Should().Be(SqlBinaryOperator.Add);

        add.Left.Should().BeOfType<SqlLiteralExpression>();
        var mul = add.Right.Should().BeOfType<SqlBinaryExpression>().Subject;
        mul.Operator.Should().Be(SqlBinaryOperator.Multiply);
    }

    [Fact]
    public void Parse_ParenthesizedExpression_ChangesGrouping()
    {
        // (1 + 2) * 3 should parse as (1 + 2) * 3
        var expr = ParseExpr("(1 + 2) * 3");
        var mul = expr.Should().BeOfType<SqlBinaryExpression>().Subject;
        mul.Operator.Should().Be(SqlBinaryOperator.Multiply);

        var add = mul.Left.Should().BeOfType<SqlBinaryExpression>().Subject;
        add.Operator.Should().Be(SqlBinaryOperator.Add);
    }

    [Fact]
    public void Parse_NegativeNumber_ReturnsUnaryExpression()
    {
        var expr = ParseExpr("-5");
        var unary = expr.Should().BeOfType<SqlUnaryExpression>().Subject;
        unary.Operator.Should().Be(SqlUnaryOperator.Negate);
        unary.Operand.Should().BeOfType<SqlLiteralExpression>();
    }

    [Fact]
    public void Parse_NotExpression_ReturnsUnaryNot()
    {
        var expr = ParseWhere("NOT Active");
        var unary = expr.Should().BeOfType<SqlUnaryExpression>().Subject;
        unary.Operator.Should().Be(SqlUnaryOperator.Not);
    }

    [Fact]
    public void Parse_AndOrPrecedence_AndBindsTighter()
    {
        // a OR b AND c should parse as a OR (b AND c)
        var expr = ParseWhere("a = 1 OR b = 2 AND c = 3");
        var or = expr.Should().BeOfType<SqlBinaryExpression>().Subject;
        or.Operator.Should().Be(SqlBinaryOperator.Or);
        or.Right.Should().BeOfType<SqlBinaryExpression>()
            .Which.Operator.Should().Be(SqlBinaryOperator.And);
    }

    [Fact]
    public void Parse_Between_ReturnsBetweenExpression()
    {
        var expr = ParseWhere("x BETWEEN 1 AND 10");
        var between = expr.Should().BeOfType<SqlBetweenExpression>().Subject;
        between.IsNegated.Should().BeFalse();
        between.Low.Should().BeOfType<SqlLiteralExpression>();
        between.High.Should().BeOfType<SqlLiteralExpression>();
    }

    [Fact]
    public void Parse_NotBetween_ReturnsNegatedBetweenExpression()
    {
        var expr = ParseWhere("x NOT BETWEEN 1 AND 10");
        var between = expr.Should().BeOfType<SqlBetweenExpression>().Subject;
        between.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void Parse_InValueList_ReturnsInExpression()
    {
        var expr = ParseWhere("x IN (1, 2, 3)");
        var inExpr = expr.Should().BeOfType<SqlInExpression>().Subject;
        inExpr.IsNegated.Should().BeFalse();
        inExpr.Values.Should().HaveCount(3);
        inExpr.Subquery.Should().BeNull();
    }

    [Fact]
    public void Parse_NotIn_ReturnsNegatedInExpression()
    {
        var expr = ParseWhere("x NOT IN (1, 2)");
        var inExpr = expr.Should().BeOfType<SqlInExpression>().Subject;
        inExpr.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void Parse_Like_ReturnsLikeExpression()
    {
        var expr = ParseWhere("Name LIKE '%test%'");
        var like = expr.Should().BeOfType<SqlLikeExpression>().Subject;
        like.IsNegated.Should().BeFalse();
        like.Pattern.Should().BeOfType<SqlLiteralExpression>();
    }

    [Fact]
    public void Parse_IsNull_ReturnsIsNullExpression()
    {
        var expr = ParseWhere("Email IS NULL");
        var isNull = expr.Should().BeOfType<SqlIsNullExpression>().Subject;
        isNull.IsNegated.Should().BeFalse();
    }

    [Fact]
    public void Parse_IsNotNull_ReturnsNegatedIsNullExpression()
    {
        var expr = ParseWhere("Email IS NOT NULL");
        var isNull = expr.Should().BeOfType<SqlIsNullExpression>().Subject;
        isNull.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void Parse_CaseWhen_ReturnsCaseExpression()
    {
        var expr = ParseExpr("CASE WHEN x = 1 THEN 'a' ELSE 'b' END");
        var caseExpr = expr.Should().BeOfType<SqlCaseExpression>().Subject;
        caseExpr.Input.Should().BeNull(); // searched CASE (no input)
        caseExpr.WhenClauses.Should().HaveCount(1);
        caseExpr.ElseResult.Should().NotBeNull();
    }

    [Fact]
    public void Parse_SimpleCaseExpression_HasInputExpression()
    {
        var expr = ParseExpr("CASE Status WHEN 'active' THEN 1 WHEN 'inactive' THEN 0 END");
        var caseExpr = expr.Should().BeOfType<SqlCaseExpression>().Subject;
        caseExpr.Input.Should().NotBeNull();
        caseExpr.WhenClauses.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_Cast_ReturnsCastExpression()
    {
        var expr = ParseExpr("CAST(x AS INT)");
        var cast = expr.Should().BeOfType<SqlCastExpression>().Subject;
        cast.Operand.Should().BeOfType<SqlColumnReferenceExpression>();
        cast.TargetType.Should().Be("INT");
    }

    [Fact]
    public void Parse_FunctionCall_ReturnsFunctionCallExpression()
    {
        var expr = ParseExpr("UPPER(Name)");
        var func = expr.Should().BeOfType<SqlFunctionCallExpression>().Subject;
        func.FunctionName.Should().Be("UPPER");
        func.Arguments.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_Exists_ReturnsExistsExpression()
    {
        var expr = ParseWhere("EXISTS (SELECT 1 FROM Users)");
        var exists = expr.Should().BeOfType<SqlExistsExpression>().Subject;
        exists.IsNegated.Should().BeFalse();
        exists.Subquery.Should().NotBeNull();
    }

    [Fact]
    public void Parse_NotExists_ReturnsNegatedExistsExpression()
    {
        var expr = ParseWhere("NOT EXISTS (SELECT 1 FROM Users)");
        var exists = expr.Should().BeOfType<SqlExistsExpression>().Subject;
        exists.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void Parse_ComparisonOperators_ParseAllOperators()
    {
        ParseWhere("a = 1").Should().BeOfType<SqlBinaryExpression>()
            .Which.Operator.Should().Be(SqlBinaryOperator.Equal);
        ParseWhere("a <> 1").Should().BeOfType<SqlBinaryExpression>()
            .Which.Operator.Should().Be(SqlBinaryOperator.NotEqual);
        ParseWhere("a < 1").Should().BeOfType<SqlBinaryExpression>()
            .Which.Operator.Should().Be(SqlBinaryOperator.LessThan);
        ParseWhere("a > 1").Should().BeOfType<SqlBinaryExpression>()
            .Which.Operator.Should().Be(SqlBinaryOperator.GreaterThan);
        ParseWhere("a <= 1").Should().BeOfType<SqlBinaryExpression>()
            .Which.Operator.Should().Be(SqlBinaryOperator.LessOrEqual);
        ParseWhere("a >= 1").Should().BeOfType<SqlBinaryExpression>()
            .Which.Operator.Should().Be(SqlBinaryOperator.GreaterOrEqual);
    }

    [Fact]
    public void Parse_DottedColumnReference_ParsesTableAndColumn()
    {
        var expr = ParseExpr("u.Name");
        var colRef = expr.Should().BeOfType<SqlColumnReferenceExpression>().Subject;
        colRef.TableAlias.Should().Be("u");
        colRef.ColumnName.Should().Be("Name");
    }

    [Fact]
    public void Parse_ThreePartColumnReference_ParsesSchemaTableColumn()
    {
        var expr = ParseExpr("dbo.Users.Id");
        var colRef = expr.Should().BeOfType<SqlColumnReferenceExpression>().Subject;
        colRef.SchemaName.Should().Be("dbo");
        colRef.TableAlias.Should().Be("Users");
        colRef.ColumnName.Should().Be("Id");
    }
}
