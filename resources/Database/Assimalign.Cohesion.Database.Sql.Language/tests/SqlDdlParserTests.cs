using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public class SqlDdlParserTests
{
    private readonly SqlQueryParser _parser = new();

    [Fact]
    public void Parse_CreateTable_ParsesTableAndColumns()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE Users (Id INT, Name TEXT);");
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>();

        create.CommandType.ShouldBe(SqlQueryCommandType.Create);
        create.Table.TableName.ShouldBe("Users");
        create.IfNotExists.ShouldBeFalse();
        create.Columns.Count.ShouldBe(2);
        create.Columns[0].ColumnName.ShouldBe("Id");
        create.Columns[0].DataType.ShouldBe("INT");
        create.Columns[1].ColumnName.ShouldBe("Name");
        create.Columns[1].DataType.ShouldBe("TEXT");
    }

    [Fact]
    public void Parse_CreateTableIfNotExists_SetsIfNotExists()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE IF NOT EXISTS Users (Id INT);");
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>();

        create.IfNotExists.ShouldBeTrue();
        create.Table.TableName.ShouldBe("Users");
    }

    [Fact]
    public void Parse_CreateTableWithConstraints_ParsesConstraints()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE Users (Id INT PRIMARY KEY NOT NULL, Name TEXT DEFAULT 'unknown');");
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>();

        create.Columns.Count.ShouldBe(2);
        create.Columns[0].IsPrimaryKey.ShouldBeTrue();
        create.Columns[0].IsNullable.ShouldBeFalse();
        create.Columns[1].DefaultValue.ShouldNotBeNull();
    }

    [Fact]
    public void Parse_CreateTableSchemaQualified_ParsesSchema()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "CREATE TABLE dbo.Users (Id INT);");
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>();

        create.Table.SchemaName.ShouldBe("dbo");
        create.Table.TableName.ShouldBe("Users");
    }

    [Fact]
    public void Parse_DropTable_ParsesTableReference()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DROP TABLE Users;");
        var drop = statement.SqlExpression.ShouldBeOfType<SqlDropTableExpression>();

        drop.CommandType.ShouldBe(SqlQueryCommandType.Drop);
        drop.Table.TableName.ShouldBe("Users");
        drop.IfExists.ShouldBeFalse();
    }

    [Fact]
    public void Parse_DropTableIfExists_SetsIfExists()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "DROP TABLE IF EXISTS Users;");
        var drop = statement.SqlExpression.ShouldBeOfType<SqlDropTableExpression>();

        drop.IfExists.ShouldBeTrue();
        drop.Table.TableName.ShouldBe("Users");
    }

    [Fact]
    public void Parse_AlterTableAddColumn_ParsesAddAction()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "ALTER TABLE Users ADD Email TEXT;");
        var alter = statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>();

        alter.CommandType.ShouldBe(SqlQueryCommandType.Alter);
        alter.Table.TableName.ShouldBe("Users");
        var addAction = alter.Action.ShouldBeOfType<SqlAlterAddColumnAction>();
        addAction.Column.ColumnName.ShouldBe("Email");
        addAction.Column.DataType.ShouldBe("TEXT");
    }

    [Fact]
    public void Parse_AlterTableDropColumn_ParsesDropAction()
    {
        var statement = (SqlQueryStatement)_parser.Parse(
            "ALTER TABLE Users DROP COLUMN Email;");
        var alter = statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>();

        var dropAction = alter.Action.ShouldBeOfType<SqlAlterDropColumnAction>();
        dropAction.ColumnName.ShouldBe("Email");
    }
}
