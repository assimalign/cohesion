using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Sql.Tests;

public class SqlDeleteParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_DeleteWithWhere_ParsesTableAndWhere()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DELETE FROM Users WHERE Id = 1;");
        var delete = statement.SqlExpression.ShouldBeOfType<SqlDeleteExpression>();

        delete.CommandType.ShouldBe(SqlQueryCommandType.Delete);
        delete.Table.TableName.ShouldBe("Users");
        delete.Where.ShouldNotBeNull();

        var binary = delete.Where.ShouldBeOfType<SqlBinaryExpression>();
        binary.Operator.ShouldBe(SqlBinaryOperator.Equal);
    }

    [Fact]
    public void Parse_DeleteWithoutWhere_HasNullWhere()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DELETE FROM Users;");
        var delete = statement.SqlExpression.ShouldBeOfType<SqlDeleteExpression>();

        delete.Table.TableName.ShouldBe("Users");
        delete.Where.ShouldBeNull();
    }

    [Fact]
    public void Parse_DeleteFromSchemaQualifiedTable_ParsesSchema()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DELETE FROM dbo.Users WHERE Id = 1;");
        var delete = statement.SqlExpression.ShouldBeOfType<SqlDeleteExpression>();

        delete.Table.SchemaName.ShouldBe("dbo");
        delete.Table.TableName.ShouldBe("Users");
    }
}
