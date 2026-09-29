using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlQuotedIdentifierTests
{
    [Fact]
    public void Select_QuotedNamesAndAliases_ContainIdentifierValuesWithoutDelimiters()
    {
        var select = Parse("SELECT \"JOIN\".\"SELECT\" AS \"FROM\" FROM \"my schema\".\"order details\" AS \"JOIN\" WHERE \"JOIN\".\"SELECT\" = @p ORDER BY \"FROM\";")
            .ShouldBeOfType<SqlSelectExpression>();

        select.From!.TableName.ShouldBe("order details");
        select.From.SchemaName.ShouldBe("my schema");
        select.From.Alias.ShouldBe("JOIN");
        select.Columns[0].Alias.ShouldBe("FROM");
        var column = select.Columns[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>();
        column.TableAlias.ShouldBe("JOIN");
        column.ColumnName.ShouldBe("SELECT");
        select.OrderBy[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>().ColumnName.ShouldBe("FROM");
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("TRUE")]
    [InlineData("CASE")]
    [InlineData("COUNT")]
    [InlineData("WITH")]
    public void Select_QuotedReservedWords_RemainColumnIdentifiers(string name)
    {
        var select = Parse($"SELECT \"{name}\" FROM \"values\";").ShouldBeOfType<SqlSelectExpression>();
        select.Columns[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>().ColumnName.ShouldBe(name);
    }

    [Fact]
    public void QuotedCommand_DoesNotBecomeAStatementKeyword()
    {
        var statement = new SqlQueryParser().Parse("\"SELECT\" * FROM \"items\";").ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0002");
    }

    [Fact]
    public void InsertAndUpdate_QuotedTargetColumns_PreserveExactNames()
    {
        var insert = Parse("INSERT INTO \"row data\" (\"key value\", \"select\") VALUES (1, 'value');")
            .ShouldBeOfType<SqlInsertExpression>();
        insert.Table.TableName.ShouldBe("row data");
        insert.Columns.ShouldBe(new[] { "key value", "select" });

        var update = Parse("UPDATE \"row data\" SET \"select\" = 'changed' WHERE \"key value\" = 1;")
            .ShouldBeOfType<SqlUpdateExpression>();
        update.Table.TableName.ShouldBe("row data");
        update.Assignments[0].ColumnName.ShouldBe("select");
        update.Where.ShouldBeOfType<SqlBinaryExpression>().Left.ShouldBeOfType<SqlColumnReferenceExpression>()
            .ColumnName.ShouldBe("key value");

        var delete = Parse("DELETE FROM \"row data\" WHERE \"key value\" = 1;")
            .ShouldBeOfType<SqlDeleteExpression>();
        delete.Table.TableName.ShouldBe("row data");
    }

    [Fact]
    public void CreateTable_QuotedConstraintAndReferenceNames_AreUnquoted()
    {
        var create = Parse("CREATE TABLE \"dbo\".\"child rows\" (\"Key\" INT, \"parent id\" INT, CONSTRAINT \"PK child\" PRIMARY KEY (\"Key\"), CONSTRAINT \"FK child\" FOREIGN KEY (\"parent id\") REFERENCES \"dbo\".\"parent rows\" (\"Key\"));")
            .ShouldBeOfType<SqlCreateTableExpression>();
        create.Table.TableName.ShouldBe("child rows");
        create.Table.SchemaName.ShouldBe("dbo");
        create.Columns[1].ColumnName.ShouldBe("parent id");
        create.Constraints[0].Name.ShouldBe("PK child");
        create.Constraints[0].Columns.ShouldBe(new[] { "Key" });
        create.Constraints[1].Name.ShouldBe("FK child");
        create.Constraints[1].ReferencedTable!.TableName.ShouldBe("parent rows");
        create.Constraints[1].ReferencedColumns.ShouldBe(new[] { "Key" });
    }

    [Fact]
    public void IndexAlterAndDrop_QuotedDdlNames_AreUnquoted()
    {
        var create = Parse("CREATE INDEX \"IX name\" ON \"row data\" (\"select\");")
            .ShouldBeOfType<SqlCreateIndexExpression>();
        create.IndexName.ShouldBe("IX name");
        create.Table.TableName.ShouldBe("row data");
        create.Columns.ShouldBe(new[] { "select" });

        var alter = Parse("ALTER TABLE \"row data\" DROP COLUMN \"select\";")
            .ShouldBeOfType<SqlAlterTableExpression>();
        alter.Table.TableName.ShouldBe("row data");
        alter.Action.ShouldBeOfType<SqlAlterDropColumnAction>().ColumnName.ShouldBe("select");

        var dropIndex = Parse("DROP INDEX \"IX name\" ON \"row data\";").ShouldBeOfType<SqlDropIndexExpression>();
        dropIndex.IndexName.ShouldBe("IX name");
        dropIndex.Table.TableName.ShouldBe("row data");
        Parse("DROP TABLE \"row data\";").ShouldBeOfType<SqlDropTableExpression>().Table.TableName.ShouldBe("row data");
    }

    [Fact]
    public void QuotedDot_IsPartOfOneIdentifier()
    {
        var select = Parse("SELECT \"field.part\" FROM \"table.part\";").ShouldBeOfType<SqlSelectExpression>();
        select.From!.TableName.ShouldBe("table.part");
        select.From.SchemaName.ShouldBeNull();
        select.Columns[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>().ColumnName.ShouldBe("field.part");
    }

    private static SqlQueryExpression Parse(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "SQL0002" || diagnostic.Code == "SQL0003" || diagnostic.Code == "COHDBL001");
        return statement.SqlExpression;
    }
}
