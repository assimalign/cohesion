using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlExpressionDefaultParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: expressions leave column constraints unconsumed")]
    [InlineData("1 + 2")]
    [InlineData("(1 + 2)")]
    [InlineData("-(1 + 2)")]
    [InlineData("-1")]
    [InlineData("CURRENT_DATE")]
    [InlineData("CURRENT_TIME")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("LOCALTIME")]
    [InlineData("LOCALTIMESTAMP")]
    [InlineData("NOW()")]
    [InlineData("UPPER('x')")]
    [InlineData("GEN_RANDOM_UUID()")]
    [InlineData("CAST('1' AS INT)")]
    public void Parse_ExpressionDefault_ShouldKeepFollowingConstraints(string expression)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"CREATE TABLE c (v INT DEFAULT {expression} NOT NULL CHECK (v > 0) REFERENCES p(id));")
            .ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var column = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Columns.Single();
        column.IsNullable.ShouldBeFalse();
        column.Constraints.Count.ShouldBe(2);
        if (expression == "-1")
        {
            column.DefaultValue.ShouldBeOfType<SqlUnaryExpression>().Operand.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT: a following COLLATE belongs to the column")]
    public void Parse_DefaultBeforeCollation_ShouldRetainColumnCollation()
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("CREATE TABLE c (v TEXT DEFAULT UPPER('x' COLLATE binary) COLLATE case_insensitive NOT NULL);")
            .ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Columns.Single().CollationName.ShouldBe("case_insensitive");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Foreign keys: all five delete actions parse in every declaration")]
    [InlineData("CASCADE", SqlReferentialAction.Cascade)]
    [InlineData("RESTRICT", SqlReferentialAction.Restrict)]
    [InlineData("NO ACTION", SqlReferentialAction.NoAction)]
    [InlineData("SET NULL", SqlReferentialAction.SetNull)]
    [InlineData("SET DEFAULT", SqlReferentialAction.SetDefault)]
    public void Parse_DeleteAction_ShouldRetainAction(string action, SqlReferentialAction expected)
    {
        // Arrange
        string[] declarations =
        [
            $"CREATE TABLE c (v INT REFERENCES p(id) ON DELETE {action});",
            $"CREATE TABLE c (v INT, FOREIGN KEY(v) REFERENCES p(id) ON DELETE {action});",
            $"ALTER TABLE c ADD CONSTRAINT fk FOREIGN KEY(v) REFERENCES p(id) ON DELETE {action};",
        ];

        // Act / Assert
        foreach (string sql in declarations)
        {
            var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
            statement.Diagnostics.ShouldBeEmpty();
            var constraint = statement.SqlExpression is SqlCreateTableExpression create ? create.Constraints.Single()
                : statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>().Action.ShouldBeOfType<SqlAlterAddConstraintAction>().Constraint;
            constraint.OnDelete.ShouldBe(expected);
        }
        new SqlQueryParser().Parse("CREATE TABLE no (action INT);").Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DEFAULT and delete actions: excluded variants retain capability errors")]
    [InlineData("CREATE TABLE c (v INT REFERENCES p(id) ON DELETE SET NULL (v));")]
    [InlineData("CREATE TABLE c (v INT REFERENCES p(id) ON DELETE SET DEFAULT (v));")]
    [InlineData("CREATE TABLE c (v TIMESTAMP DEFAULT CURRENT_TIMESTAMP(3));")]
    [InlineData("CREATE TABLE c (v TIMESTAMP DEFAULT LOCALTIMESTAMP(3));")]
    [InlineData("CREATE TABLE c (v INT DEFAULT (SELECT id FROM p));")]
    public void Parse_ExcludedForm_ShouldReportCapability(string sql)
    {
        // Act / Assert
        new SqlQueryParser().Parse(sql).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHDBL001");
    }
}
