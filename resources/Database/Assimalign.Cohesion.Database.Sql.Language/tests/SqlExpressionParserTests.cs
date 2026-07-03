using Shouldly;
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
        var literal = expr.ShouldBeOfType<SqlLiteralExpression>();
        literal.Value.ShouldBe("42");
        literal.LiteralType.ShouldBe(SqlLiteralType.Integer);
    }

    [Fact]
    public void Parse_StringLiteral_ReturnsLiteralExpression()
    {
        var expr = ParseExpr("'hello'");
        var literal = expr.ShouldBeOfType<SqlLiteralExpression>();
        literal.LiteralType.ShouldBe(SqlLiteralType.String);
    }

    [Fact]
    public void Parse_FloatLiteral_ReturnsLiteralExpression()
    {
        var expr = ParseExpr("3.14");
        var literal = expr.ShouldBeOfType<SqlLiteralExpression>();
        literal.Value.ShouldBe("3.14");
        literal.LiteralType.ShouldBe(SqlLiteralType.Float);
    }

    [Fact]
    public void Parse_NullLiteral_ReturnsNullLiteral()
    {
        var expr = ParseExpr("NULL");
        var literal = expr.ShouldBeOfType<SqlLiteralExpression>();
        literal.LiteralType.ShouldBe(SqlLiteralType.Null);
    }

    [Fact]
    public void Parse_BooleanLiteral_ReturnsBooleanLiteral()
    {
        var expr = ParseExpr("TRUE");
        var literal = expr.ShouldBeOfType<SqlLiteralExpression>();
        literal.LiteralType.ShouldBe(SqlLiteralType.Boolean);
        literal.Value.ShouldBe("TRUE");
    }

    [Fact]
    public void Parse_Parameter_ReturnsParameterExpression()
    {
        var expr = ParseExpr("@userId");
        var param = expr.ShouldBeOfType<SqlParameterExpression>();
        param.ParameterName.ShouldBe("@userId");
    }

    [Fact]
    public void Parse_DollarParameter_ReturnsParameterExpression()
    {
        var expr = ParseExpr("$1");
        var param = expr.ShouldBeOfType<SqlParameterExpression>();
        param.ParameterName.ShouldBe("$1");
    }

    [Fact]
    public void Parse_ArithmeticPrecedence_MultiplicationBeforeAddition()
    {
        // 1 + 2 * 3 should parse as 1 + (2 * 3)
        var expr = ParseExpr("1 + 2 * 3");
        var add = expr.ShouldBeOfType<SqlBinaryExpression>();
        add.Operator.ShouldBe(SqlBinaryOperator.Add);

        add.Left.ShouldBeOfType<SqlLiteralExpression>();
        var mul = add.Right.ShouldBeOfType<SqlBinaryExpression>();
        mul.Operator.ShouldBe(SqlBinaryOperator.Multiply);
    }

    [Fact]
    public void Parse_ParenthesizedExpression_ChangesGrouping()
    {
        // (1 + 2) * 3 should parse as (1 + 2) * 3
        var expr = ParseExpr("(1 + 2) * 3");
        var mul = expr.ShouldBeOfType<SqlBinaryExpression>();
        mul.Operator.ShouldBe(SqlBinaryOperator.Multiply);

        var add = mul.Left.ShouldBeOfType<SqlBinaryExpression>();
        add.Operator.ShouldBe(SqlBinaryOperator.Add);
    }

    [Fact]
    public void Parse_NegativeNumber_ReturnsUnaryExpression()
    {
        var expr = ParseExpr("-5");
        var unary = expr.ShouldBeOfType<SqlUnaryExpression>();
        unary.Operator.ShouldBe(SqlUnaryOperator.Negate);
        unary.Operand.ShouldBeOfType<SqlLiteralExpression>();
    }

    [Fact]
    public void Parse_NotExpression_ReturnsUnaryNot()
    {
        var expr = ParseWhere("NOT Active");
        var unary = expr.ShouldBeOfType<SqlUnaryExpression>();
        unary.Operator.ShouldBe(SqlUnaryOperator.Not);
    }

    [Fact]
    public void Parse_AndOrPrecedence_AndBindsTighter()
    {
        // a OR b AND c should parse as a OR (b AND c)
        var expr = ParseWhere("a = 1 OR b = 2 AND c = 3");
        var or = expr.ShouldBeOfType<SqlBinaryExpression>();
        or.Operator.ShouldBe(SqlBinaryOperator.Or);
        or.Right.ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.And);
    }

    [Fact]
    public void Parse_Between_ReturnsBetweenExpression()
    {
        var expr = ParseWhere("x BETWEEN 1 AND 10");
        var between = expr.ShouldBeOfType<SqlBetweenExpression>();
        between.IsNegated.ShouldBeFalse();
        between.Low.ShouldBeOfType<SqlLiteralExpression>();
        between.High.ShouldBeOfType<SqlLiteralExpression>();
    }

    [Fact]
    public void Parse_NotBetween_ReturnsNegatedBetweenExpression()
    {
        var expr = ParseWhere("x NOT BETWEEN 1 AND 10");
        var between = expr.ShouldBeOfType<SqlBetweenExpression>();
        between.IsNegated.ShouldBeTrue();
    }

    [Fact]
    public void Parse_InValueList_ReturnsInExpression()
    {
        var expr = ParseWhere("x IN (1, 2, 3)");
        var inExpr = expr.ShouldBeOfType<SqlInExpression>();
        inExpr.IsNegated.ShouldBeFalse();
        inExpr.Values!.Count.ShouldBe(3);
        inExpr.Subquery.ShouldBeNull();
    }

    [Fact]
    public void Parse_NotIn_ReturnsNegatedInExpression()
    {
        var expr = ParseWhere("x NOT IN (1, 2)");
        var inExpr = expr.ShouldBeOfType<SqlInExpression>();
        inExpr.IsNegated.ShouldBeTrue();
    }

    [Fact]
    public void Parse_Like_ReturnsLikeExpression()
    {
        var expr = ParseWhere("Name LIKE '%test%'");
        var like = expr.ShouldBeOfType<SqlLikeExpression>();
        like.IsNegated.ShouldBeFalse();
        like.Pattern.ShouldBeOfType<SqlLiteralExpression>();
    }

    [Fact]
    public void Parse_IsNull_ReturnsIsNullExpression()
    {
        var expr = ParseWhere("Email IS NULL");
        var isNull = expr.ShouldBeOfType<SqlIsNullExpression>();
        isNull.IsNegated.ShouldBeFalse();
    }

    [Fact]
    public void Parse_IsNotNull_ReturnsNegatedIsNullExpression()
    {
        var expr = ParseWhere("Email IS NOT NULL");
        var isNull = expr.ShouldBeOfType<SqlIsNullExpression>();
        isNull.IsNegated.ShouldBeTrue();
    }

    [Fact]
    public void Parse_CaseWhen_ReturnsCaseExpression()
    {
        var expr = ParseExpr("CASE WHEN x = 1 THEN 'a' ELSE 'b' END");
        var caseExpr = expr.ShouldBeOfType<SqlCaseExpression>();
        caseExpr.Input.ShouldBeNull(); // searched CASE (no input)
        caseExpr.WhenClauses.Count.ShouldBe(1);
        caseExpr.ElseResult.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_SimpleCaseExpression_HasInputExpression()
    {
        var expr = ParseExpr("CASE Status WHEN 'active' THEN 1 WHEN 'inactive' THEN 0 END");
        var caseExpr = expr.ShouldBeOfType<SqlCaseExpression>();
        caseExpr.Input.ShouldNotBeNull();
        caseExpr.WhenClauses.Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_Cast_ReturnsCastExpression()
    {
        var expr = ParseExpr("CAST(x AS INT)");
        var cast = expr.ShouldBeOfType<SqlCastExpression>();
        cast.Operand.ShouldBeOfType<SqlColumnReferenceExpression>();
        cast.TargetType.ShouldBe("INT");
    }

    [Fact]
    public void Parse_FunctionCall_ReturnsFunctionCallExpression()
    {
        var expr = ParseExpr("UPPER(Name)");
        var func = expr.ShouldBeOfType<SqlFunctionCallExpression>();
        func.FunctionName.ShouldBe("UPPER");
        func.Arguments.Count.ShouldBe(1);
    }

    [Fact]
    public void Parse_Exists_ReturnsExistsExpression()
    {
        var expr = ParseWhere("EXISTS (SELECT 1 FROM Users)");
        var exists = expr.ShouldBeOfType<SqlExistsExpression>();
        exists.IsNegated.ShouldBeFalse();
        exists.Subquery.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_NotExists_ReturnsNegatedExistsExpression()
    {
        var expr = ParseWhere("NOT EXISTS (SELECT 1 FROM Users)");
        var exists = expr.ShouldBeOfType<SqlExistsExpression>();
        exists.IsNegated.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ComparisonOperators_ParseAllOperators()
    {
        ParseWhere("a = 1").ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.Equal);
        ParseWhere("a <> 1").ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.NotEqual);
        ParseWhere("a < 1").ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.LessThan);
        ParseWhere("a > 1").ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.GreaterThan);
        ParseWhere("a <= 1").ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.LessOrEqual);
        ParseWhere("a >= 1").ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.GreaterOrEqual);
    }

    [Fact]
    public void Parse_DottedColumnReference_ParsesTableAndColumn()
    {
        var expr = ParseExpr("u.Name");
        var colRef = expr.ShouldBeOfType<SqlColumnReferenceExpression>();
        colRef.TableAlias.ShouldBe("u");
        colRef.ColumnName.ShouldBe("Name");
    }

    [Fact]
    public void Parse_ThreePartColumnReference_ParsesSchemaTableColumn()
    {
        var expr = ParseExpr("dbo.Users.Id");
        var colRef = expr.ShouldBeOfType<SqlColumnReferenceExpression>();
        colRef.SchemaName.ShouldBe("dbo");
        colRef.TableAlias.ShouldBe("Users");
        colRef.ColumnName.ShouldBe("Id");
    }
}
