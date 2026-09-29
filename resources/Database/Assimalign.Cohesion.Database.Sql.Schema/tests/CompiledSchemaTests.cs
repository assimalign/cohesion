using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public class CompiledSchemaTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: lowers the complete declaration into a stable immutable document")]
    public void Compile_WithValidDeclaration_ShouldProduceStableDocument()
    {
        SqlCompiledSchema schema = CompileOrders();

        schema.Format.ShouldBe(SqlCompiledSchema.CurrentFormat);
        schema.Name.ShouldBe("orders-db");
        schema.Model.ShouldBe(EngineModel.Sql);
        schema.Tables.Select(table => table.Name).ShouldBe(["order_lines", "orders"]);
        schema.Tables[1].RowType.ShouldStartWith("Assimalign.Cohesion.Database.Sql.Schema.Tests:");
        schema.Tables[1].RowType.ShouldNotContain("Version=");
        schema.Tables[1].Columns.Select(column => column.Name).ShouldBe(["Id", "Total", "CustomerId"]);
        schema.Tables[0].Constraints.ShouldHaveSingleItem().ReferencedObject.ShouldBe("orders");
        schema.Tables.ShouldAllBe(table => table.Owner == DatabaseObjectOwner.Schema);
        schema.Tables.SelectMany(table => table.Indexes).ShouldAllBe(index => index.Owner == DatabaseObjectOwner.Schema);
        schema.Tables[0].Constraints.ShouldHaveSingleItem().Owner.ShouldBe(DatabaseObjectOwner.Schema);
        schema.Functions.ShouldHaveSingleItem().Body.CanonicalText.ShouldContain("lambda<");
        schema.Triggers.ShouldHaveSingleItem().Table.ShouldBe("orders");
        schema.Principals.ShouldHaveSingleItem().Grants.ShouldHaveSingleItem().Objects.ShouldBe(["order_lines", "orders"]);
        schema.Hash.Length.ShouldBe(64);

        string document = SqlCompiledSchemaSerializer.Serialize(schema);
        schema.CanonicalDocument.ShouldBe(document);
        document.ShouldNotContain("\"collections\"");
        document.ShouldNotContain("\"hash\"");
        document.ShouldNotContain("\"canonicalDocument\"");
        SqlCompiledSchema restored = SqlCompiledSchemaSerializer.Deserialize(document);
        restored.Hash.ShouldBe(schema.Hash);
        restored.Tables.ShouldAllBe(table => table.Owner == DatabaseObjectOwner.Schema);
        SqlCompiledSchemaSerializer.Serialize(restored).ShouldBe(document);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: declaration ordering does not change the content hash")]
    public void Compile_WithEquivalentDeclarationOrder_ShouldKeepHashStable()
    {
        SqlCompiledSchema first = CompileOrders(linesFirst: false);
        SqlCompiledSchema second = CompileOrders(linesFirst: true);

        second.Hash.ShouldBe(first.Hash);
        SqlCompiledSchemaSerializer.Serialize(second).ShouldBe(SqlCompiledSchemaSerializer.Serialize(first));
    }

    [Fact]
    public void Compile_WithNestedGenericAndArrayIdentities_ShouldPreserveCanonicalTypeNames()
    {
        SqlCompiledSchema schema = SqlSchema.Compile("generic-identities", database =>
        {
            database.Table<GenericRow<GenericValue<int[,], string[]>[]>>("arrays", table => table.Key(row => row.Id));
            database.Table<GenericContainer<int>.Nested<string>>("nested", table => table.Key(row => row.Id));
            database.Type<GenericValue<decimal?[,], byte[][]>>(type => type.Decimal(18, 2));
        });
        const string local = "Assimalign.Cohesion.Database.Sql.Schema.Tests:Assimalign.Cohesion.Database.Sql.Schema.Tests.CompiledSchemaTests+";
        const string system = "System.Private.CoreLib:System.";

        schema.Tables[0].RowType.ShouldBe(
            local + "GenericRow`1[" + local + "GenericValue`2[" + system + "Int32[,];" + system + "String[]][]]");
        schema.Tables[1].RowType.ShouldBe(
            local + "GenericContainer`1+Nested`1[" + system + "Int32;" + system + "String]");
        schema.Types.ShouldHaveSingleItem().Name.ShouldBe(
            local + "GenericValue`2[" + system + "Nullable`1[" + system + "Decimal][,];" + system + "Byte[][]]");
        schema.CanonicalDocument.ShouldNotContain("Version=");
        SqlCompiledSchemaSerializer.Deserialize(schema.CanonicalDocument).Hash.ShouldBe(schema.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: typed diagnostics name the offending declaration")]
    public void Compile_WithInvalidDeclarations_ShouldReportTypedNamedErrors()
    {
        ISqlSchema declaration = SqlSchema.Create("invalid", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Principal("reader", principal => principal.Grant(SqlPermission.Read, "missing"));
        });

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(
            () => SqlSchemaCompiler.Compile(declaration, EngineModel.Sql));

        exception.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.DuplicateDeclaration && error.Declaration == "orders");
        exception.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.UnknownReference && error.Declaration == "reader.missing");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: invalid references remain typed and named")]
    public void Compile_WithInvalidReferences_ShouldReportPreciseDiagnostics()
    {
        ISqlSchema relational = SqlSchema.Create("invalid-references", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<InvalidOrderLine>("lines", table =>
            {
                table.Key(line => line.Id);
                table.References<Order>(line => line.OrderId);
                table.References<Order>(line => line.OrderId);
            });
        });
        SqlSchemaValidationException relationalError = Should.Throw<SqlSchemaValidationException>(
            () => SqlSchemaCompiler.Compile(relational, EngineModel.Sql));

        relationalError.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.DuplicateDeclaration &&
            error.Declaration == "FK_lines_orders_OrderId");
        relationalError.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.ModelMismatch &&
            error.Declaration == "lines.OrderId");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: nondeterministic expressions are rejected")]
    public void Compile_WithNondeterministicExpression_ShouldRejectNamedFunction()
    {
        ISqlSchema declaration = SqlSchema.Create("invalid-expression", database =>
            database.Function("current_time", () => DateTime.Now));

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(
            () => SqlSchemaCompiler.Compile(declaration, EngineModel.Sql));

        exception.Errors.ShouldContain(error =>
            error.Code == SqlSchemaValidationErrorCode.UnsupportedExpression &&
            error.Declaration == "current_time");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: parameter names and grant declaration order do not affect hashes")]
    public void Compile_WithEquivalentParameterAndGrantOrder_ShouldKeepHashStable()
    {
        SqlCompiledSchema first = CompileFunctionAndGrants(reverse: false);
        SqlCompiledSchema second = CompileFunctionAndGrants(reverse: true);

        second.Hash.ShouldBe(first.Hash);
        first.Functions.ShouldHaveSingleItem().Parameters.ShouldHaveSingleItem().Name.ShouldBe("arg0");
        first.Principals.ShouldHaveSingleItem().Grants.ShouldHaveSingleItem().Objects.ShouldBe(["order_lines", "orders"]);
    }

    [Theory]
    [InlineData(EngineModel.KeyValueStore)]
    [InlineData(EngineModel.Document)]
    [InlineData(EngineModel.Custom)]
    public void Compile_WithNonSqlModel_ShouldRejectModel(EngineModel model)
    {
        ISqlSchema declaration = SqlSchema.Create("orders", _ => { });
        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(
            () => SqlSchemaCompiler.Compile(declaration, model));

        exception.Errors.ShouldHaveSingleItem().Code.ShouldBe(SqlSchemaValidationErrorCode.ModelMismatch);
    }
    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: add table and index use deterministic safe order")]
    public void Plan_FromEmptySchema_ShouldAddTableBeforeIndex()
    {
        SqlCompiledSchema desired = SqlSchema.Compile("catalog", database =>
            database.Table<Order>("orders", table =>
            {
                table.Key(order => order.Id);
                table.Index(order => order.CustomerId);
            }));

        SqlSchemaMigrationPlan plan = SqlSchemaMigrationPlanner.Plan(null, desired);

        plan.Operations.Select(operation => operation.Kind).ShouldBe([
            SqlSchemaMigrationOperationKind.AddTable,
            SqlSchemaMigrationOperationKind.AddIndex]);
        plan.Operations.ShouldAllBe(operation => operation.Safety == SqlSchemaMigrationSafety.Safe);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: nullable add column is safe")]
    public void Plan_WithNullableColumnAdded_ShouldCreateSafeAddColumn()
    {
        SqlCompiledSchema current = CompileVersion(includeAge: false);
        SqlCompiledSchema desired = CompileVersion(includeAge: true);

        SqlSchemaMigrationOperation operation = SqlSchemaMigrationPlanner.Plan(current, desired).Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AddColumn);
        operation.ObjectName.ShouldBe("Age");
        operation.Safety.ShouldBe(SqlSchemaMigrationSafety.Safe);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: drop column is refused until the schema opts in")]
    public void Plan_WithColumnDropped_ShouldRequireDestructiveOptIn()
    {
        SqlCompiledSchema current = CompileVersion(includeAge: true);
        SqlCompiledSchema refused = CompileVersion(includeAge: false);
        SqlCompiledSchema allowed = CompileVersion(includeAge: false, allowDestructive: true);

        SqlSchemaMigrationException exception = Should.Throw<SqlSchemaMigrationException>(
            () => SqlSchemaMigrationPlanner.Plan(current, refused));
        exception.Message.ShouldContain("People.Age");

        SqlSchemaMigrationOperation operation = SqlSchemaMigrationPlanner.Plan(current, allowed).Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.DropColumn);
        operation.Safety.ShouldBe(SqlSchemaMigrationSafety.Destructive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: changed indexes drop before add")]
    public void Plan_WithIndexChanged_ShouldDropBeforeAdd()
    {
        SqlCompiledSchema current = CompileIndex(useAge: false);
        SqlCompiledSchema desired = CompileIndex(useAge: true);

        SqlSchemaMigrationPlan plan = SqlSchemaMigrationPlanner.Plan(current, desired);

        plan.Operations.Select(operation => operation.Kind).ShouldBe([
            SqlSchemaMigrationOperationKind.DropIndex,
            SqlSchemaMigrationOperationKind.AddIndex]);
        plan.Operations.Select(operation => operation.ObjectName).ShouldBe([
            "IX_People_Name",
            "IX_People_Age"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: unsupported metadata never reports false convergence")]
    public void Plan_WithMetadataOnlyChange_ShouldFailExplicitly()
    {
        SqlCompiledSchema current = SqlSchema.Compile("orders", database =>
            database.Table<Order>("orders", table => table.Key(order => order.Id)));
        SqlCompiledSchema desired = SqlSchema.Compile("orders", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Extension("sql.collation", "ordinal");
        });

        SqlSchemaMigrationException exception = Should.Throw<SqlSchemaMigrationException>(
            () => SqlSchemaMigrationPlanner.Plan(current, desired));

        exception.Message.ShouldContain("model extensions");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: column reordering is a destructive table alteration")]
    public void Plan_WithColumnReordered_ShouldRequireDestructiveAlter()
    {
        SqlCompiledSchema current = ReorderedSchema(["Id", "Name"], allowDestructive: false);
        SqlCompiledSchema refused = ReorderedSchema(["Name", "Id"], allowDestructive: false);
        SqlCompiledSchema allowed = ReorderedSchema(["Name", "Id"], allowDestructive: true);

        Should.Throw<SqlSchemaMigrationException>(() => SqlSchemaMigrationPlanner.Plan(current, refused))
            .Message.ShouldContain("AlterTable");
        SqlSchemaMigrationOperation operation = SqlSchemaMigrationPlanner.Plan(current, allowed).Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AlterTable);
        operation.Safety.ShouldBe(SqlSchemaMigrationSafety.Destructive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: SQL identifier casing does not create destructive operations")]
    public void Plan_WithSqlIdentifierCasingChanged_ShouldBeEmpty()
    {
        SqlCompiledSchema current = CasedSchema("People", "Id", "PK_People", "IX_People_Id");
        SqlCompiledSchema desired = CasedSchema("people", "id", "pk_people", "ix_people_id");

        SqlSchemaMigrationPlan plan = SqlSchemaMigrationPlanner.Plan(current, desired);

        plan.IsEmpty.ShouldBeTrue();
        plan.SourceHash.ShouldBe(current.Hash);
        plan.TargetHash.ShouldBe(desired.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: inserted columns require an ordered table alteration")]
    public void Plan_WithColumnInsertedBeforeExistingColumn_ShouldRequireDestructiveAlter()
    {
        SqlCompiledSchema current = ReorderedSchema(["Id", "Name"], allowDestructive: false);
        SqlCompiledSchema desired = OrderedSchema(["Id", "Age", "Name"], allowDestructive: false);

        SqlSchemaMigrationException exception = Should.Throw<SqlSchemaMigrationException>(
            () => SqlSchemaMigrationPlanner.Plan(current, desired));

        exception.Message.ShouldContain("AlterTable");
    }

    private static SqlCompiledSchema CompileOrders(bool linesFirst = false)
    {
        return SqlSchema.Compile("orders-db", database =>
        {
            database.Type<Money>(type => type.Decimal(18, 2));
            if (linesFirst)
            {
                AddLines(database);
                AddOrders(database);
            }
            else
            {
                AddOrders(database);
                AddLines(database);
            }

            database.Function("order_identity", (long orderId) => orderId);
            database.Trigger<Order>(SqlTriggerEvent.AfterInsert, (transaction, row) => transaction.Audit("order.placed", row.Id));
            database.Principal("reader", principal => principal.Grant(SqlPermission.Read, "orders", "order_lines"));
            database.Extension("sql.collation", "ordinal");
        });
    }

    private static void AddOrders(ISqlSchemaBuilder database)
    {
        database.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.Column(order => order.Total);
            table.Index(order => order.CustomerId);
        });
    }

    private static void AddLines(ISqlSchemaBuilder database)
    {
        database.Table<OrderLine>("order_lines", table =>
        {
            table.Key(line => line.Id);
            table.References<Order>(line => line.OrderId);
            table.Column(line => line.Quantity);
            table.Column(line => line.UnitPrice);
        });
    }

    private static SqlCompiledSchema CompileVersion(bool includeAge, bool allowDestructive = false)
        => SqlSchema.Compile("people", database =>
        {
            if (allowDestructive)
            {
                database.AllowDestructiveChanges();
            }

            database.Table<Person>("People", table =>
            {
                table.Key(person => person.Id);
                table.Column(person => person.Name);
                if (includeAge)
                {
                    table.Column(person => person.Age);
                }
            });
        });

    private static SqlCompiledSchema CompileIndex(bool useAge)
        => SqlSchema.Compile("people", database =>
            database.Table<Person>("People", table =>
            {
                table.Key(person => person.Id);
                table.Column(person => person.Name);
                table.Column(person => person.Age);
                if (useAge)
                {
                    table.Index(person => person.Age);
                }
                else
                {
                    table.Index(person => person.Name);
                }
            }));

    private static SqlCompiledSchema CompileFunctionAndGrants(bool reverse)
        => SqlSchema.Compile("orders-db", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<OrderLine>("order_lines", table => table.Key(line => line.Id));
            if (reverse)
            {
                database.Function("identity", (long renamed) => renamed);
                database.Principal("reader", principal =>
                {
                    principal.Grant(SqlPermission.Read, "orders");
                    principal.Grant(SqlPermission.Read, "order_lines");
                });
            }
            else
            {
                database.Function("identity", (long value) => value);
                database.Principal("reader", principal =>
                {
                    principal.Grant(SqlPermission.Read, "order_lines");
                    principal.Grant(SqlPermission.Read, "orders");
                });
            }
        });

    private static SqlCompiledSchema ReorderedSchema(string[] columnOrder, bool allowDestructive)
        => OrderedSchema(columnOrder, allowDestructive);

    private static SqlCompiledSchema OrderedSchema(string[] columnOrder, bool allowDestructive)
    {
        CompiledSchemaColumn id = new("Id", DatabaseType.Int64, IsNullable: false);
        CompiledSchemaColumn name = new("Name", DatabaseType.String, IsNullable: true);
        CompiledSchemaColumn age = new("Age", DatabaseType.Int32, IsNullable: true);
        CompiledSchemaColumn[] columns = columnOrder.Select(column => column switch
        {
            "Id" => id,
            "Name" => name,
            _ => age,
        }).ToArray();
        return new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            "people",
            EngineModel.Sql,
            allowDestructive,
            [],
            [new CompiledSchemaTable("People", "Tests:Person", columns, new CompiledSchemaKey("PK_People", ["Id"]), [], [])],
            [],
            [],
            [],
            []);
    }

    private static SqlCompiledSchema CasedSchema(
        string tableName,
        string columnName,
        string keyName,
        string indexName)
        => new(
            SqlCompiledSchema.CurrentFormat,
            "people",
            EngineModel.Sql,
            allowsDestructiveChanges: false,
            [],
            [new CompiledSchemaTable(
                tableName,
                "Tests:Person",
                [new CompiledSchemaColumn(columnName, DatabaseType.Int64, IsNullable: false)],
                new CompiledSchemaKey(keyName, [columnName]),
                [new CompiledSchemaIndex(indexName, [columnName])],
                [])],
            [],
            [],
            [],
            []);

    private readonly record struct Money(decimal Amount);
    private sealed record Order(long Id, long CustomerId, Money Total);
    private sealed record OrderLine(long Id, long OrderId, int Quantity, Money UnitPrice);
    private sealed record InvalidOrderLine(long Id, string OrderId);
    private sealed record Person(long Id, string Name, int? Age);
    private sealed record GenericRow<T>(long Id);
    private sealed record GenericValue<TFirst, TSecond>(TFirst First, TSecond Second);
    private sealed class GenericContainer<T>
    {
        internal sealed record Nested<TNested>(long Id);
    }
}
