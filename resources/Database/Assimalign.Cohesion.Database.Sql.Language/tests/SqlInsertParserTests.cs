using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Sql.Tests;

public class SqlInsertParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_InsertWithValues_ParsesTableAndValues()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) VALUES (1, 'Alice');");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.CommandType.ShouldBe(SqlQueryCommandType.Insert);
        insert.Table.TableName.ShouldBe("Users");
        insert.Columns.ShouldNotBeNull();
        insert.Columns.Count.ShouldBe(2);
        insert.Columns![0].ShouldBe("Id");
        insert.Columns[1].ShouldBe("Name");

        insert.Values.ShouldNotBeNull();
        insert.Values.Count.ShouldBe(1);
        insert.Values![0].Count.ShouldBe(2);

        var val0 = insert.Values[0][0].ShouldBeOfType<SqlLiteralExpression>();
        val0.Value.ShouldBe("1");

        var val1 = insert.Values[0][1].ShouldBeOfType<SqlLiteralExpression>();
        val1.LiteralType.ShouldBe(SqlLiteralType.String);
    }

    [Fact]
    public void Parse_InsertMultipleRows_ParsesAllRows()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.Values!.Count.ShouldBe(2);
        insert.Values![0].Count.ShouldBe(2);
        insert.Values[1].Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_InsertSelect_ParsesSelectSource()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) SELECT Id, Name FROM OldUsers;");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.SelectSource.ShouldNotBeNull();
        insert.Values.ShouldBeNull();
        insert.SelectSource!.Columns.Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_InsertWithSchemaQualifiedTable_ParsesSchemaAndTable()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO dbo.Users (Id) VALUES (1);");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.Table.SchemaName.ShouldBe("dbo");
        insert.Table.TableName.ShouldBe("Users");
    }

    [Fact]
    public void Parse_InsertWithoutColumnList_HasNullColumns()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users VALUES (1, 'Alice');");
        var insert = statement.SqlExpression.ShouldBeOfType<SqlInsertExpression>();

        insert.Columns.ShouldBeNull();
        insert.Values!.Count.ShouldBe(1);
    }
}
