using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public class SqlUpdateParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_SimpleUpdate_ParsesTableSetAndWhere()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "UPDATE Users SET Name = 'Bob' WHERE Id = 1;");
        var update = statement.SqlExpression.ShouldBeOfType<SqlUpdateExpression>();

        update.CommandType.ShouldBe(SqlQueryCommandType.Update);
        update.Table.TableName.ShouldBe("Users");
        update.Assignments.Count.ShouldBe(1);
        update.Assignments[0].ColumnName.ShouldBe("Name");

        var val = update.Assignments[0].Value.ShouldBeOfType<SqlLiteralExpression>();
        val.LiteralType.ShouldBe(SqlLiteralType.String);

        update.Where.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_UpdateMultipleAssignments_ParsesAll()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "UPDATE dbo.Users SET Name = 'Bob', Status = 'active' WHERE Id = 1;");
        var update = statement.SqlExpression.ShouldBeOfType<SqlUpdateExpression>();

        update.Table.SchemaName.ShouldBe("dbo");
        update.Assignments.Count.ShouldBe(2);
        update.Assignments[0].ColumnName.ShouldBe("Name");
        update.Assignments[1].ColumnName.ShouldBe("Status");
    }

    [Fact]
    public void Parse_UpdateWithoutWhere_HasNullWhere()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "UPDATE Users SET Status = 'inactive';");
        var update = statement.SqlExpression.ShouldBeOfType<SqlUpdateExpression>();

        update.Where.ShouldBeNull();
    }
}
