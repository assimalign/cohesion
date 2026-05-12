using FluentAssertions;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Sql.Tests;

public class SqlUpdateParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_SimpleUpdate_ParsesTableSetAndWhere()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "UPDATE Users SET Name = 'Bob' WHERE Id = 1;");
        var update = statement.SqlExpression.Should().BeOfType<SqlUpdateExpression>().Subject;

        update.CommandType.Should().Be(SqlQueryCommandType.Update);
        update.Table.TableName.Should().Be("Users");
        update.Assignments.Should().HaveCount(1);
        update.Assignments[0].ColumnName.Should().Be("Name");

        var val = update.Assignments[0].Value.Should().BeOfType<SqlLiteralExpression>().Subject;
        val.LiteralType.Should().Be(SqlLiteralType.String);

        update.Where.Should().NotBeNull();
    }

    [Fact]
    public void Parse_UpdateMultipleAssignments_ParsesAll()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "UPDATE dbo.Users SET Name = 'Bob', Status = 'active' WHERE Id = 1;");
        var update = statement.SqlExpression.Should().BeOfType<SqlUpdateExpression>().Subject;

        update.Table.SchemaName.Should().Be("dbo");
        update.Assignments.Should().HaveCount(2);
        update.Assignments[0].ColumnName.Should().Be("Name");
        update.Assignments[1].ColumnName.Should().Be("Status");
    }

    [Fact]
    public void Parse_UpdateWithoutWhere_HasNullWhere()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "UPDATE Users SET Status = 'inactive';");
        var update = statement.SqlExpression.Should().BeOfType<SqlUpdateExpression>().Subject;

        update.Where.Should().BeNull();
    }
}
