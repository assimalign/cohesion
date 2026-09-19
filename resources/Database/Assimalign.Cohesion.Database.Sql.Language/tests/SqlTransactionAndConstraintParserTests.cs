using System.Linq;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTransactionAndConstraintParserTests
{
    [Theory]
    [InlineData("BEGIN;", SqlQueryCommandType.Begin)]
    [InlineData("begin transaction;", SqlQueryCommandType.Begin)]
    [InlineData("COMMIT;", SqlQueryCommandType.Commit)]
    [InlineData("COMMIT TRANSACTION;", SqlQueryCommandType.Commit)]
    [InlineData("ROLLBACK;", SqlQueryCommandType.Rollback)]
    [InlineData("ROLLBACK TRANSACTION;", SqlQueryCommandType.Rollback)]
    public void Transaction_ParsesOnlyItsCommand(string sql, SqlQueryCommandType command)
    {
        var statement = Parse(sql);
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlTransactionExpression>().CommandType.ShouldBe(command);
    }

    [Theory]
    [InlineData("BEGIN SAVEPOINT marker;")]
    [InlineData("ROLLBACK TO marker;")]
    [InlineData("COMMIT WORK;")]
    public void Transaction_UnsupportedSuffix_IsAnError(string sql) =>
        Parse(sql).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0003");

    [Fact]
    public void TransactionKeyword_RequiresAControlCommand() =>
        Parse("TRANSACTION;").Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0002");

    [Fact]
    public void CreateTable_NormalizesNamedAndUnnamedColumnAndTableConstraints()
    {
        const string sql = "CREATE TABLE t (id INT PRIMARY KEY, parent_id INT REFERENCES parent(id) ON DELETE CASCADE, email TEXT UNIQUE, qty INT CHECK (qty > 0), CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parent(id) ON DELETE RESTRICT);";
        var statement = Parse(sql);
        statement.Diagnostics.ShouldBeEmpty();
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>();
        create.Columns.Count.ShouldBe(4);
        create.Constraints.Count.ShouldBe(5);
        create.Constraints[0].Kind.ShouldBe(SqlConstraintKind.PrimaryKey);
        var reference = create.Constraints[1];
        reference.Columns.ShouldBe(["parent_id"]);
        reference.ReferencedTable!.TableName.ShouldBe("parent");
        reference.ReferencedColumns.ShouldBe(["id"]);
        reference.OnDelete.ShouldBe(SqlReferentialAction.Cascade);
        create.Constraints[2].Kind.ShouldBe(SqlConstraintKind.Unique);
        create.Constraints[3].CheckExpression.ShouldBeOfType<SqlBinaryExpression>();
        create.Constraints[3].CheckExpressionText.ShouldBe("qty > 0");
        create.Constraints[4].Name.ShouldBe("fk_parent");
        create.Constraints[4].OnDelete.ShouldBe(SqlReferentialAction.Restrict);
    }

    [Fact]
    public void CreateTable_CompositeConstraints_PreserveColumnOrderAndPredicateSource()
    {
        var statement = Parse("CREATE TABLE child (tenant INT, id INT, qty INT, CONSTRAINT pk PRIMARY KEY (tenant, id), CONSTRAINT uq UNIQUE (id, tenant), CONSTRAINT fk FOREIGN KEY (tenant, id) REFERENCES dbo.parent(tenant, id), CONSTRAINT ck CHECK ((qty + 1) > 0 AND qty < 100));");
        statement.Diagnostics.ShouldBeEmpty();
        var constraints = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Constraints;
        constraints[0].Columns.ShouldBe(["tenant", "id"]);
        constraints[1].Columns.ShouldBe(["id", "tenant"]);
        constraints[2].ReferencedTable!.SchemaName.ShouldBe("dbo");
        constraints[2].OnDelete.ShouldBe(SqlReferentialAction.Restrict);
        constraints[3].CheckExpressionText.ShouldBe("(qty + 1) > 0 AND qty < 100");
    }

    [Theory]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (parent_id) REFERENCES parent(id) ON DELETE CASCADE;", SqlConstraintKind.ForeignKey)]
    [InlineData("ALTER TABLE t ADD UNIQUE (email);", SqlConstraintKind.Unique)]
    [InlineData("ALTER TABLE t ADD CHECK (qty > 0);", SqlConstraintKind.Check)]
    public void AlterTable_AddConstraint_Parses(string sql, SqlConstraintKind kind)
    {
        var statement = Parse(sql);
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>().Action
            .ShouldBeOfType<SqlAlterAddConstraintAction>().Constraint.Kind.ShouldBe(kind);
    }

    [Fact]
    public void AlterTable_AddColumn_PreservesNamedColumnConstraint()
    {
        var statement = Parse("ALTER TABLE t ADD COLUMN parent_id INT CONSTRAINT fk REFERENCES parent(id);");
        statement.Diagnostics.ShouldBeEmpty();
        var column = statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>().Action
            .ShouldBeOfType<SqlAlterAddColumnAction>().Column;
        column.Constraints.Single().Name.ShouldBe("fk");
        column.Constraints.Single().Columns.ShouldBe(["parent_id"]);
    }

    [Fact]
    public void AlterTable_DropConstraint_PreservesName()
    {
        var statement = Parse("ALTER TABLE t DROP CONSTRAINT fk;");
        statement.Diagnostics.ShouldBeEmpty();
        statement.SqlExpression.ShouldBeOfType<SqlAlterTableExpression>().Action
            .ShouldBeOfType<SqlAlterDropConstraintAction>().ConstraintName.ShouldBe("fk");
    }

    [Theory]
    [InlineData("CREATE TABLE t (parent_id INT REFERENCES parent(id) ON UPDATE CASCADE);")]
    [InlineData("CREATE TABLE t (parent_id INT REFERENCES parent(id) ON DELETE CASCADE ON UPDATE RESTRICT);")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (parent_id) REFERENCES parent(id) ON UPDATE RESTRICT;")]
    public void OnUpdate_RemainsUnsupported(string sql)
    {
        SqlLanguageProfile.Instance.Supports("ON UPDATE").ShouldBeFalse();
        Parse(sql).Diagnostics.Single(diagnostic => diagnostic.Code == "COHDBL001")
            .Message!.ShouldContain("ON UPDATE");
    }

    [Theory]
    [InlineData("CREATE TABLE t (parent_id INT REFERENCES parent());")]
    [InlineData("CREATE TABLE t (id INT, FOREIGN KEY (id) REFERENCES parent(a, b));")]
    [InlineData("CREATE TABLE t (id INT, CONSTRAINT ck CHECK (id > 0);")]
    [InlineData("ALTER TABLE t ADD UNIQUE ();")]
    [InlineData("CREATE TABLE t (id INT REFERENCES parent(id) ON DELETE SET NULL);")]
    [InlineData("CREATE TABLE t (id INT CHECK ());")]
    [InlineData("DROP TABLE t CASCADE;")]
    public void MalformedConstraint_ReturnsAnErrorInsteadOfThrowing(string sql)
    {
        var statement = Should.NotThrow(() => Parse(sql));
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);
}
