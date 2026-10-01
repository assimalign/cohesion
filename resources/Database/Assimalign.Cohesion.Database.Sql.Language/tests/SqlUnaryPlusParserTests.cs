using System.Linq;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// ISO unary plus: an operator on any operand, while a <c>+</c> directly before a numeric
/// literal stays part of the literal.
/// </summary>
public sealed class SqlUnaryPlusParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Unary plus: + on a non-literal operand is a unary plus node")]
    [InlineData("+a", typeof(SqlColumnReferenceExpression))]
    [InlineData("+(1 + 2)", typeof(SqlBinaryExpression))]
    [InlineData("+@p", typeof(SqlParameterExpression))]
    [InlineData("+$1", typeof(SqlParameterExpression))]
    [InlineData("+'abc'", typeof(SqlLiteralExpression))]
    [InlineData("+TRUE", typeof(SqlLiteralExpression))]
    [InlineData("+(1)", typeof(SqlLiteralExpression))]
    [InlineData("+ABS(a)", typeof(SqlFunctionCallExpression))]
    [InlineData("+ -a", typeof(SqlUnaryExpression))]
    public void Parse_UnaryPlus_ShouldBuildPlusNode(string expression, System.Type operand)
    {
        // Act
        var statement = Parse($"SELECT {expression} FROM t;");

        // Assert
        Errors(statement).ShouldBeEmpty();
        var plus = Column(statement).ShouldBeOfType<SqlUnaryExpression>();
        plus.Operator.ShouldBe(SqlUnaryOperator.Plus);
        plus.Operand.ShouldBeOfType(operand);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Unary plus: + directly before a numeric literal stays part of the literal")]
    [InlineData("+1", "1", SqlLiteralType.Integer)]
    [InlineData("+ /* trivia */ 2", "2", SqlLiteralType.Integer)]
    [InlineData("+1.5", "1.5", SqlLiteralType.Float)]
    [InlineData("+.5", ".5", SqlLiteralType.Float)]
    public void Parse_SignedNumericLiteral_ShouldStayLiteral(string expression, string value, SqlLiteralType type)
    {
        // Act
        var statement = Parse($"SELECT {expression} FROM t;");

        // Assert
        Errors(statement).ShouldBeEmpty();
        var literal = Column(statement).ShouldBeOfType<SqlLiteralExpression>();
        literal.Value.ShouldBe(value);
        literal.LiteralType.ShouldBe(type);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Unary plus: binds tighter than binary operators and nests with other signs")]
    public void Parse_UnaryPlus_ShouldBindAsAUnaryOperator()
    {
        // Act
        var statement = Parse("SELECT a + +b * 2, - +a, + +1 FROM t;");

        // Assert
        Errors(statement).ShouldBeEmpty();
        var columns = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns;
        var add = columns[0].Expression.ShouldBeOfType<SqlBinaryExpression>();
        add.Operator.ShouldBe(SqlBinaryOperator.Add);
        var multiply = add.Right.ShouldBeOfType<SqlBinaryExpression>();
        multiply.Operator.ShouldBe(SqlBinaryOperator.Multiply);
        multiply.Left.ShouldBeOfType<SqlUnaryExpression>().Operator.ShouldBe(SqlUnaryOperator.Plus);

        var negate = columns[1].Expression.ShouldBeOfType<SqlUnaryExpression>();
        negate.Operator.ShouldBe(SqlUnaryOperator.Negate);
        negate.Operand.ShouldBeOfType<SqlUnaryExpression>().Operator.ShouldBe(SqlUnaryOperator.Plus);

        var plus = columns[2].Expression.ShouldBeOfType<SqlUnaryExpression>();
        plus.Operator.ShouldBe(SqlUnaryOperator.Plus);
        plus.Operand.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Unary plus: a CHECK accepts unary plus")]
    public void Parse_UnaryPlusInCheck_ShouldParse()
    {
        // Act
        var statement = Parse("CREATE TABLE t (qty INT, CHECK (+qty > 0));");

        // Assert
        Errors(statement).ShouldBeEmpty();
        var check = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Constraints.Single();
        check.CheckExpression.ShouldBeOfType<SqlBinaryExpression>().Left.ShouldBeOfType<SqlUnaryExpression>()
            .Operator.ShouldBe(SqlUnaryOperator.Plus);
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);

    private static SqlExpression Column(SqlQueryStatement statement)
        => statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression;

    private static System.Collections.Generic.List<Diagnostic> Errors(SqlQueryStatement statement)
        => statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
}
