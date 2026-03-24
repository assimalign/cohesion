using FluentAssertions;
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
        var insert = statement.SqlExpression.Should().BeOfType<SqlInsertExpression>().Subject;

        insert.CommandType.Should().Be(SqlQueryCommandType.Insert);
        insert.Table.TableName.Should().Be("Users");
        insert.Columns.Should().NotBeNull();
        insert.Columns.Should().HaveCount(2);
        insert.Columns![0].Should().Be("Id");
        insert.Columns[1].Should().Be("Name");

        insert.Values.Should().NotBeNull();
        insert.Values.Should().HaveCount(1);
        insert.Values![0].Should().HaveCount(2);

        var val0 = insert.Values[0][0].Should().BeOfType<SqlLiteralExpression>().Subject;
        val0.Value.Should().Be("1");

        var val1 = insert.Values[0][1].Should().BeOfType<SqlLiteralExpression>().Subject;
        val1.LiteralType.Should().Be(SqlLiteralType.String);
    }

    [Fact]
    public void Parse_InsertMultipleRows_ParsesAllRows()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) VALUES (1, 'Alice'), (2, 'Bob');");
        var insert = statement.SqlExpression.Should().BeOfType<SqlInsertExpression>().Subject;

        insert.Values.Should().HaveCount(2);
        insert.Values![0].Should().HaveCount(2);
        insert.Values[1].Should().HaveCount(2);
    }

    [Fact]
    public void Parse_InsertSelect_ParsesSelectSource()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users (Id, Name) SELECT Id, Name FROM OldUsers;");
        var insert = statement.SqlExpression.Should().BeOfType<SqlInsertExpression>().Subject;

        insert.SelectSource.Should().NotBeNull();
        insert.Values.Should().BeNull();
        insert.SelectSource!.Columns.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_InsertWithSchemaQualifiedTable_ParsesSchemaAndTable()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO dbo.Users (Id) VALUES (1);");
        var insert = statement.SqlExpression.Should().BeOfType<SqlInsertExpression>().Subject;

        insert.Table.SchemaName.Should().Be("dbo");
        insert.Table.TableName.Should().Be("Users");
    }

    [Fact]
    public void Parse_InsertWithoutColumnList_HasNullColumns()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "INSERT INTO Users VALUES (1, 'Alice');");
        var insert = statement.SqlExpression.Should().BeOfType<SqlInsertExpression>().Subject;

        insert.Columns.Should().BeNull();
        insert.Values.Should().HaveCount(1);
    }
}
