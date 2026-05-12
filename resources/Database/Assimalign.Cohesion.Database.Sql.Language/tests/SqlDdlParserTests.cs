using FluentAssertions;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Sql.Tests;

public class SqlDdlParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_CreateTable_ParsesTableAndColumns()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE Users (Id INT, Name TEXT);");
        var create = statement.SqlExpression.Should().BeOfType<SqlCreateTableExpression>().Subject;

        create.CommandType.Should().Be(SqlQueryCommandType.Create);
        create.Table.TableName.Should().Be("Users");
        create.IfNotExists.Should().BeFalse();
        create.Columns.Should().HaveCount(2);
        create.Columns[0].ColumnName.Should().Be("Id");
        create.Columns[0].DataType.Should().Be("INT");
        create.Columns[1].ColumnName.Should().Be("Name");
        create.Columns[1].DataType.Should().Be("TEXT");
    }

    [Fact]
    public void Parse_CreateTableIfNotExists_SetsIfNotExists()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE IF NOT EXISTS Users (Id INT);");
        var create = statement.SqlExpression.Should().BeOfType<SqlCreateTableExpression>().Subject;

        create.IfNotExists.Should().BeTrue();
        create.Table.TableName.Should().Be("Users");
    }

    [Fact]
    public void Parse_CreateTableWithConstraints_ParsesConstraints()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE Users (Id INT PRIMARY KEY NOT NULL, Name TEXT DEFAULT 'unknown');");
        var create = statement.SqlExpression.Should().BeOfType<SqlCreateTableExpression>().Subject;

        create.Columns.Should().HaveCount(2);
        create.Columns[0].IsPrimaryKey.Should().BeTrue();
        create.Columns[0].IsNullable.Should().BeFalse();
        create.Columns[1].DefaultValue.Should().NotBeNull();
    }

    [Fact]
    public void Parse_CreateTableSchemaQualified_ParsesSchema()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE dbo.Users (Id INT);");
        var create = statement.SqlExpression.Should().BeOfType<SqlCreateTableExpression>().Subject;

        create.Table.SchemaName.Should().Be("dbo");
        create.Table.TableName.Should().Be("Users");
    }

    [Fact]
    public void Parse_DropTable_ParsesTableReference()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DROP TABLE Users;");
        var drop = statement.SqlExpression.Should().BeOfType<SqlDropTableExpression>().Subject;

        drop.CommandType.Should().Be(SqlQueryCommandType.Drop);
        drop.Table.TableName.Should().Be("Users");
        drop.IfExists.Should().BeFalse();
    }

    [Fact]
    public void Parse_DropTableIfExists_SetsIfExists()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DROP TABLE IF EXISTS Users;");
        var drop = statement.SqlExpression.Should().BeOfType<SqlDropTableExpression>().Subject;

        drop.IfExists.Should().BeTrue();
        drop.Table.TableName.Should().Be("Users");
    }

    [Fact]
    public void Parse_AlterTableAddColumn_ParsesAddAction()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "ALTER TABLE Users ADD Email TEXT;");
        var alter = statement.SqlExpression.Should().BeOfType<SqlAlterTableExpression>().Subject;

        alter.CommandType.Should().Be(SqlQueryCommandType.Alter);
        alter.Table.TableName.Should().Be("Users");
        var addAction = alter.Action.Should().BeOfType<SqlAlterAddColumnAction>().Subject;
        addAction.Column.ColumnName.Should().Be("Email");
        addAction.Column.DataType.Should().Be("TEXT");
    }

    [Fact]
    public void Parse_AlterTableDropColumn_ParsesDropAction()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "ALTER TABLE Users DROP COLUMN Email;");
        var alter = statement.SqlExpression.Should().BeOfType<SqlAlterTableExpression>().Subject;

        var dropAction = alter.Action.Should().BeOfType<SqlAlterDropColumnAction>().Subject;
        dropAction.ColumnName.Should().Be("Email");
    }
}
