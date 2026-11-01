using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public class SqlSchemaStatementRendererTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Schema statements: every storage type renders and resolves its full modifiers")]
    public void CreateTable_EveryStorageType_ShouldRoundTripResolvedType()
    {
        // Arrange
        foreach (var type in Enum.GetValues<DatabaseType>())
        {
            if (type == DatabaseType.Null) { continue; }
            int? length = type is DatabaseType.Character or DatabaseType.String or DatabaseType.Binary ? 20 : null;
            int? precision = type == DatabaseType.Decimal ? 12 : null;
            int? scale = type == DatabaseType.Decimal ? 3 : null;
            var table = new CompiledSchemaTable("types", "Row",
                [new CompiledSchemaColumn("value", type, true, length, precision, scale)], null, [], []);

            // Act
            string sql = SqlSchemaStatementRenderer.CreateTable(table);
            var statement = new SqlQueryParser().Parse(sql);

            // Assert
            statement.Diagnostics.ShouldBeEmpty(sql);
            var resolved = statement.Expression.ShouldBeOfType<SqlCreateTableExpression>().Columns[0].ResolvedType.ShouldNotBeNull();
            resolved.Type.ShouldBe(type);
            resolved.MaxLength.ShouldBe(length);
            resolved.Precision.ShouldBe(precision);
            resolved.Scale.ShouldBe(scale);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema statements: CREATE TABLE is deterministic and executable")]
    public void CreateTable_CompiledTable_ShouldRenderDeterministicRequestText()
    {
        // Arrange
        var table = new CompiledSchemaTable(
            "orders",
            "Example.Order",
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("total", DatabaseType.Decimal, IsNullable: false, Precision: 18, Scale: 2),
                new CompiledSchemaColumn("note", DatabaseType.String, IsNullable: true, MaxLength: 200),
            ],
            new CompiledSchemaKey("pk_orders", ["id"]),
            [],
            []);

        // Act
        string sql = SqlSchemaStatementRenderer.CreateTable(table);
        var request = SqlQueryRequest.FromSql(sql);

        // Assert
        sql.ShouldBe(
            "CREATE TABLE IF NOT EXISTS dbo.orders (id BIGINT PRIMARY KEY NOT NULL, " +
            "total DECIMAL(18,2) NOT NULL, note VARCHAR(200) NULL);");
        request.Statement.SqlExpression.Text.ShouldBe(sql);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema statements: primary keys are implicitly non-null")]
    public void CreateTable_NullablePrimaryKey_ShouldRenderNotNull()
    {
        // Arrange
        var table = new CompiledSchemaTable(
            "orders",
            "Example.Order",
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: true)],
            new CompiledSchemaKey("pk_orders", ["id"]),
            [],
            []);

        // Act
        string sql = SqlSchemaStatementRenderer.CreateTable(table);

        // Assert
        sql.ShouldBe("CREATE TABLE IF NOT EXISTS dbo.orders (id BIGINT PRIMARY KEY NOT NULL);");
        Should.NotThrow(() => SqlQueryRequest.FromSql(sql));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema statements: table, column, and index operations are stable")]
    public void Render_SupportedOperations_ShouldProduceExecutableText()
    {
        // Arrange
        var column = new CompiledSchemaColumn("created_at", DatabaseType.DateTimeOffset, IsNullable: false);
        var index = new CompiledSchemaIndex("ix_orders_tenant_created", ["tenant_id", "created_at"], IsUnique: true);

        // Act
        string[] statements =
        [
            SqlSchemaStatementRenderer.AddColumn("orders", column),
            SqlSchemaStatementRenderer.DropColumn("orders", "created_at"),
            SqlSchemaStatementRenderer.CreateIndex("orders", index),
            SqlSchemaStatementRenderer.DropIndex("orders", index.Name),
            SqlSchemaStatementRenderer.DropTable("orders"),
        ];

        // Assert
        statements.ShouldBe([
            "ALTER TABLE dbo.orders ADD COLUMN created_at TIMESTAMPTZ NOT NULL;",
            "ALTER TABLE dbo.orders DROP COLUMN created_at;",
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_orders_tenant_created ON dbo.orders (tenant_id, created_at);",
            "DROP INDEX IF EXISTS ix_orders_tenant_created ON dbo.orders;",
            "DROP TABLE IF EXISTS dbo.orders;",
        ]);

        foreach (string statement in statements)
        {
            Should.NotThrow(() => SqlQueryRequest.FromSql(statement));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema statements: unsupported schema shapes fail explicitly")]
    public void Render_UnsupportedShape_ShouldFailPrecisely()
    {
        // Arrange: a column whose declared type is not a type name never reaches the statement.
        var customColumn = new CompiledSchemaColumn(
            "amount",
            DatabaseType.Decimal,
            IsNullable: false,
            CustomType: "Currency); DROP TABLE dbo.ledger; --");
        var customTable = new CompiledSchemaTable(
            "ledger",
            "Example.Ledger",
            [customColumn],
            null,
            [],
            []);

        // Act / Assert
        Should.Throw<DatabaseException>(() => SqlSchemaStatementRenderer.CreateTable(customTable))
            .Message.ShouldContain("which is not a SQL type name");
        Should.Throw<DatabaseException>(() => SqlSchemaStatementRenderer.DropTable("not\"safe"))
            .Message.ShouldContain("embedded double quote");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema statements: a column declared with a domain or a built-in type name renders that name (E3)")]
    public void Render_ColumnOfDeclaredType_ShouldRenderTheName()
    {
        // Arrange
        var table = new CompiledSchemaTable(
            "ledger",
            "Example.Ledger",
            [
                new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false),
                new CompiledSchemaColumn("amount", DatabaseType.Decimal, IsNullable: true, Precision: 18, Scale: 2, CustomType: "Currency"),
                new CompiledSchemaColumn("code", DatabaseType.String, IsNullable: true, CustomType: "VARCHAR( 16 )"),
            ],
            new CompiledSchemaKey("pk_ledger", ["id"]),
            [],
            []);

        // Act
        string statement = SqlSchemaStatementRenderer.CreateTable(table);

        // Assert
        statement.ShouldBe("CREATE TABLE IF NOT EXISTS dbo.ledger (id BIGINT PRIMARY KEY NOT NULL, amount Currency NULL, code VARCHAR(16) NULL);");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Migration scripts: portable operations render in plan order")]
    public void Generate_OrderedPlan_ShouldPairForwardAndRollbackRequests()
    {
        // Arrange
        var table = new CompiledSchemaTable(
            "orders",
            "Example.Order",
            [new CompiledSchemaColumn("id", DatabaseType.Int64, IsNullable: false)],
            new CompiledSchemaKey("pk_orders", ["id"]),
            [new CompiledSchemaIndex("ix_orders_id", ["id"])],
            []);
        var plan = new SqlSchemaMigrationPlan(
            null,
            "target-hash",
            [
                new SqlSchemaMigrationOperation(
                    SqlSchemaMigrationOperationKind.AddTable,
                    SqlSchemaMigrationSafety.Safe,
                    table.Name,
                    table: table),
                new SqlSchemaMigrationOperation(
                    SqlSchemaMigrationOperationKind.AddIndex,
                    SqlSchemaMigrationSafety.Safe,
                    table.Indexes[0].Name,
                    table.Name,
                    index: table.Indexes[0]),
            ]);

        // Act
        SqlMigrationScript script = SqlMigrationScriptGenerator.Generate(plan);

        // Assert
        script.Steps.Count.ShouldBe(2);
        script.Steps[0].StatementText.ShouldBe(
            "CREATE TABLE IF NOT EXISTS dbo.orders (id BIGINT PRIMARY KEY NOT NULL);");
        script.Steps[0].RollbackStatementText.ShouldBe("DROP TABLE IF EXISTS dbo.orders;");
        script.Steps[1].StatementText.ShouldBe(
            "CREATE INDEX IF NOT EXISTS ix_orders_id ON dbo.orders (id);");
        script.Steps[1].RollbackStatementText.ShouldBe(
            "DROP INDEX IF EXISTS ix_orders_id ON dbo.orders;");
        script.Steps[0].Request.ShouldNotBeNull();
        script.Steps[1].RollbackRequest.ShouldNotBeNull();
        script.IsReversible.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Migration scripts: TYPE is rendered without reverse conversion compensation")]
    public void Generate_TypeChange_ShouldRenderTypeWithoutCompensation()
    {
        // Arrange
        var plan = new SqlSchemaMigrationPlan(
            "source-hash",
            "target-hash",
            [new SqlSchemaMigrationOperation(
                SqlSchemaMigrationOperationKind.AlterColumn,
                SqlSchemaMigrationSafety.Destructive,
                "value",
                "records",
                column: new CompiledSchemaColumn("value", DatabaseType.Int64, IsNullable: false),
                previousColumn: new CompiledSchemaColumn("value", DatabaseType.Int32, IsNullable: false))]);

        // Act
        var script = SqlMigrationScriptGenerator.Generate(plan);

        // Assert
        var step = script.Steps.ShouldHaveSingleItem();
        step.StatementText.ShouldBe("ALTER TABLE dbo.records ALTER COLUMN value TYPE BIGINT;");
        step.RollbackRequest.ShouldBeNull();
        script.IsReversible.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Migration scripts: payload names must match operation names")]
    public void Generate_MismatchedPayloadName_ShouldFailPrecisely()
    {
        // Arrange
        var plan = new SqlSchemaMigrationPlan(
            "source-hash",
            "target-hash",
            [new SqlSchemaMigrationOperation(
                SqlSchemaMigrationOperationKind.AddColumn,
                SqlSchemaMigrationSafety.Safe,
                "claimed_name",
                "orders",
                column: new CompiledSchemaColumn("actual_name", DatabaseType.String, IsNullable: true))]);

        // Act / Assert
        Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(plan))
            .Message.ShouldContain("carries a column definition named 'actual_name'");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Migration scripts: non-nullable column additions require backfill support")]
    public void Generate_NonNullableAddColumn_ShouldFailPrecisely()
    {
        // Arrange
        var plan = new SqlSchemaMigrationPlan(
            "source-hash",
            "target-hash",
            [new SqlSchemaMigrationOperation(
                SqlSchemaMigrationOperationKind.AddColumn,
                SqlSchemaMigrationSafety.Destructive,
                "required_value",
                "orders",
                column: new CompiledSchemaColumn("required_value", DatabaseType.Int64, IsNullable: false))]);

        // Act / Assert
        Should.Throw<SqlSchemaMigrationException>(() => SqlMigrationScriptGenerator.Generate(plan))
            .Message.ShouldContain("requires a non-null literal default or a backfill");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Migration scripts: compensation baseline must match source hash")]
    public void Generate_MismatchedCurrentSchema_ShouldFailPrecisely()
    {
        // Arrange
        var current = new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            "orders",
            allowsDestructiveChanges: false,
            Array.Empty<CompiledSchemaType>(),
            Array.Empty<CompiledSchemaTable>(),
            Array.Empty<CompiledSchemaPrincipal>());
        var plan = new SqlSchemaMigrationPlan("not-the-current-hash", "target-hash", []);

        // Act / Assert
        Should.Throw<SqlSchemaMigrationException>(
            () => SqlMigrationScriptGenerator.Generate(plan, current))
            .Message.ShouldContain("does not match the supplied current schema hash");
    }
}
