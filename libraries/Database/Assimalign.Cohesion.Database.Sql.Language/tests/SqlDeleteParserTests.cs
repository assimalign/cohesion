using FluentAssertions;
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
        var delete = statement.SqlExpression.Should().BeOfType<SqlDeleteExpression>().Subject;

        delete.CommandType.Should().Be(SqlQueryCommandType.Delete);
        delete.Table.TableName.Should().Be("Users");
        delete.Where.Should().NotBeNull();

        var binary = delete.Where.Should().BeOfType<SqlBinaryExpression>().Subject;
        binary.Operator.Should().Be(SqlBinaryOperator.Equal);
    }

    [Fact]
    public void Parse_DeleteWithoutWhere_HasNullWhere()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DELETE FROM Users;");
        var delete = statement.SqlExpression.Should().BeOfType<SqlDeleteExpression>().Subject;

        delete.Table.TableName.Should().Be("Users");
        delete.Where.Should().BeNull();
    }

    [Fact]
    public void Parse_DeleteFromSchemaQualifiedTable_ParsesSchema()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DELETE FROM dbo.Users WHERE Id = 1;");
        var delete = statement.SqlExpression.Should().BeOfType<SqlDeleteExpression>().Subject;

        delete.Table.SchemaName.Should().Be("dbo");
        delete.Table.TableName.Should().Be("Users");
    }
}
