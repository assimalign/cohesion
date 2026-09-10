using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Tests;

public class CompiledSchemaTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: lowers the complete declaration into a stable immutable document")]
    public void Compile_WithValidDeclaration_ShouldProduceStableDocument()
    {
        CompiledSchema schema = CompileOrders();

        schema.Format.ShouldBe(CompiledSchema.CurrentFormat);
        schema.Name.ShouldBe("orders-db");
        schema.Model.ShouldBe(EngineModel.Sql);
        schema.Tables.Select(table => table.Name).ShouldBe(["order_lines", "orders"]);
        schema.Tables[1].RowType.ShouldStartWith($"{typeof(Order).Assembly.GetName().Name}:");
        schema.Tables[1].RowType.ShouldNotContain("Version=");
        schema.Tables[1].Columns.Select(column => column.Name).ShouldBe(["Id", "Total", "CustomerId"]);
        schema.Tables[0].Constraints.ShouldHaveSingleItem().ReferencedObject.ShouldBe("orders");
        schema.Functions.ShouldHaveSingleItem().Body.CanonicalText.ShouldContain("lambda<");
        schema.Triggers.ShouldHaveSingleItem().Table.ShouldBe("orders");
        schema.Principals.ShouldHaveSingleItem().Grants.ShouldHaveSingleItem().Objects.ShouldBe(["order_lines", "orders"]);
        schema.Hash.Length.ShouldBe(64);

        string document = CompiledSchemaSerializer.Serialize(schema);
        CompiledSchema restored = CompiledSchemaSerializer.Deserialize(document);
        restored.Hash.ShouldBe(schema.Hash);
        CompiledSchemaSerializer.Serialize(restored).ShouldBe(document);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: declaration ordering does not change the content hash")]
    public void Compile_WithEquivalentDeclarationOrder_ShouldKeepHashStable()
    {
        CompiledSchema first = CompileOrders(linesFirst: false);
        CompiledSchema second = CompileOrders(linesFirst: true);

        second.Hash.ShouldBe(first.Hash);
        CompiledSchemaSerializer.Serialize(second).ShouldBe(CompiledSchemaSerializer.Serialize(first));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: typed diagnostics name the offending declaration")]
    public void Compile_WithInvalidDeclarations_ShouldReportTypedNamedErrors()
    {
        IDatabaseSchema declaration = DatabaseSchema.Create("invalid", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Principal("reader", principal => principal.Grant(Permission.Read, "missing"));
        });

        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(
            () => DatabaseSchemaCompiler.Compile(declaration, EngineModel.Sql));

        exception.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.DuplicateDeclaration && error.Declaration == "orders");
        exception.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.UnknownReference && error.Declaration == "reader.missing");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: invalid references remain typed and named")]
    public void Compile_WithInvalidReferences_ShouldReportPreciseDiagnostics()
    {
        IDatabaseSchema relational = DatabaseSchema.Create("invalid-references", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<InvalidOrderLine>("lines", table =>
            {
                table.Key(line => line.Id);
                table.References<Order>(line => line.OrderId);
                table.References<Order>(line => line.OrderId);
            });
        });
        IDatabaseSchema keyValue = DatabaseSchema.Create("invalid-collection", database =>
            database.Collection<InvalidOrderLine>("lines", collection =>
            {
                collection.Key(line => line.Id);
                collection.References<Order>(line => line.OrderId);
            }));

        DatabaseSchemaValidationException relationalError = Should.Throw<DatabaseSchemaValidationException>(
            () => DatabaseSchemaCompiler.Compile(relational, EngineModel.Sql));
        DatabaseSchemaValidationException collectionError = Should.Throw<DatabaseSchemaValidationException>(
            () => DatabaseSchemaCompiler.Compile(keyValue, EngineModel.KeyValueStore));

        relationalError.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.DuplicateDeclaration &&
            error.Declaration == "FK_lines_orders_OrderId");
        relationalError.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.ModelMismatch &&
            error.Declaration == "lines.OrderId");
        collectionError.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.ModelMismatch &&
            error.Declaration == "lines.OrderId");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: nondeterministic expressions are rejected")]
    public void Compile_WithNondeterministicExpression_ShouldRejectNamedFunction()
    {
        IDatabaseSchema declaration = DatabaseSchema.Create("invalid-expression", database =>
            database.Function("current_time", () => DateTime.Now));

        DatabaseSchemaValidationException exception = Should.Throw<DatabaseSchemaValidationException>(
            () => DatabaseSchemaCompiler.Compile(declaration, EngineModel.Sql));

        exception.Errors.ShouldContain(error =>
            error.Code == DatabaseSchemaValidationErrorCode.UnsupportedExpression &&
            error.Declaration == "current_time");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: parameter names and grant declaration order do not affect hashes")]
    public void Compile_WithEquivalentParameterAndGrantOrder_ShouldKeepHashStable()
    {
        CompiledSchema first = CompileFunctionAndGrants(reverse: false);
        CompiledSchema second = CompileFunctionAndGrants(reverse: true);

        second.Hash.ShouldBe(first.Hash);
        first.Functions.ShouldHaveSingleItem().Parameters.ShouldHaveSingleItem().Name.ShouldBe("arg0");
        first.Principals.ShouldHaveSingleItem().Grants.ShouldHaveSingleItem().Objects.ShouldBe(["order_lines", "orders"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema compiler: key-value collections bind to their declared dialect")]
    public void Compile_WithKeyValueCollection_ShouldProduceCollectionAst()
    {
        CompiledSchema schema = DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("sessions", database =>
        {
            database.Collection<SessionEntry>("sessions", collection =>
            {
                collection.Key(entry => entry.Key);
                collection.Column(entry => entry.Value);
                collection.Index(entry => entry.ExpiresAt);
            });
            database.Extension("kv.expiration", "enabled");
        }), EngineModel.KeyValueStore);

        schema.Collections.ShouldHaveSingleItem().Name.ShouldBe("sessions");
        schema.Collections[0].Key.ShouldNotBeNull().Columns.ShouldBe(["Key"]);
        schema.Collections[0].Indexes.ShouldHaveSingleItem().Name.ShouldBe("IX_sessions_ExpiresAt");
        schema.Extensions.ShouldHaveSingleItem().Name.ShouldBe("kv.expiration");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: add table and index use deterministic safe order")]
    public void Plan_FromEmptySchema_ShouldAddTableBeforeIndex()
    {
        CompiledSchema desired = DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("catalog", database =>
            database.Table<Order>("orders", table =>
            {
                table.Key(order => order.Id);
                table.Index(order => order.CustomerId);
            })), EngineModel.Sql);

        SchemaMigrationPlan plan = SchemaMigrationPlanner.Plan(null, desired);

        plan.Operations.Select(operation => operation.Kind).ShouldBe([
            SchemaMigrationOperationKind.AddTable,
            SchemaMigrationOperationKind.AddIndex]);
        plan.Operations.ShouldAllBe(operation => operation.Safety == SchemaMigrationSafety.Safe);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: nullable add column is safe")]
    public void Plan_WithNullableColumnAdded_ShouldCreateSafeAddColumn()
    {
        CompiledSchema current = CompileVersion(includeAge: false);
        CompiledSchema desired = CompileVersion(includeAge: true);

        SchemaMigrationOperation operation = SchemaMigrationPlanner.Plan(current, desired).Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe(SchemaMigrationOperationKind.AddColumn);
        operation.ObjectName.ShouldBe("Age");
        operation.Safety.ShouldBe(SchemaMigrationSafety.Safe);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: drop column is refused until the schema opts in")]
    public void Plan_WithColumnDropped_ShouldRequireDestructiveOptIn()
    {
        CompiledSchema current = CompileVersion(includeAge: true);
        CompiledSchema refused = CompileVersion(includeAge: false);
        CompiledSchema allowed = CompileVersion(includeAge: false, allowDestructive: true);

        DatabaseSchemaMigrationException exception = Should.Throw<DatabaseSchemaMigrationException>(
            () => SchemaMigrationPlanner.Plan(current, refused));
        exception.Message.ShouldContain("People.Age");

        SchemaMigrationOperation operation = SchemaMigrationPlanner.Plan(current, allowed).Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe(SchemaMigrationOperationKind.DropColumn);
        operation.Safety.ShouldBe(SchemaMigrationSafety.Destructive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: changed indexes drop before add")]
    public void Plan_WithIndexChanged_ShouldDropBeforeAdd()
    {
        CompiledSchema current = CompileIndex(useAge: false);
        CompiledSchema desired = CompileIndex(useAge: true);

        SchemaMigrationPlan plan = SchemaMigrationPlanner.Plan(current, desired);

        plan.Operations.Select(operation => operation.Kind).ShouldBe([
            SchemaMigrationOperationKind.DropIndex,
            SchemaMigrationOperationKind.AddIndex]);
        plan.Operations.Select(operation => operation.ObjectName).ShouldBe([
            "IX_People_Name",
            "IX_People_Age"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: unsupported metadata never reports false convergence")]
    public void Plan_WithMetadataOnlyChange_ShouldFailExplicitly()
    {
        CompiledSchema current = DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("orders", database =>
            database.Table<Order>("orders", table => table.Key(order => order.Id))), EngineModel.Sql);
        CompiledSchema desired = DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("orders", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Extension("sql.collation", "ordinal");
        }), EngineModel.Sql);

        DatabaseSchemaMigrationException exception = Should.Throw<DatabaseSchemaMigrationException>(
            () => SchemaMigrationPlanner.Plan(current, desired));

        exception.Message.ShouldContain("model extensions");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: column reordering is a destructive table alteration")]
    public void Plan_WithColumnReordered_ShouldRequireDestructiveAlter()
    {
        CompiledSchema current = ReorderedSchema(["Id", "Name"], allowDestructive: false);
        CompiledSchema refused = ReorderedSchema(["Name", "Id"], allowDestructive: false);
        CompiledSchema allowed = ReorderedSchema(["Name", "Id"], allowDestructive: true);

        Should.Throw<DatabaseSchemaMigrationException>(() => SchemaMigrationPlanner.Plan(current, refused))
            .Message.ShouldContain("AlterTable");
        SchemaMigrationOperation operation = SchemaMigrationPlanner.Plan(current, allowed).Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe(SchemaMigrationOperationKind.AlterTable);
        operation.Safety.ShouldBe(SchemaMigrationSafety.Destructive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: SQL identifier casing does not create destructive operations")]
    public void Plan_WithSqlIdentifierCasingChanged_ShouldBeEmpty()
    {
        CompiledSchema current = CasedSchema("People", "Id", "PK_People", "IX_People_Id");
        CompiledSchema desired = CasedSchema("people", "id", "pk_people", "ix_people_id");

        SchemaMigrationPlan plan = SchemaMigrationPlanner.Plan(current, desired);

        plan.IsEmpty.ShouldBeTrue();
        plan.SourceHash.ShouldBe(current.Hash);
        plan.TargetHash.ShouldBe(desired.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Migration planner: inserted columns require an ordered table alteration")]
    public void Plan_WithColumnInsertedBeforeExistingColumn_ShouldRequireDestructiveAlter()
    {
        CompiledSchema current = ReorderedSchema(["Id", "Name"], allowDestructive: false);
        CompiledSchema desired = OrderedSchema(["Id", "Age", "Name"], allowDestructive: false);

        DatabaseSchemaMigrationException exception = Should.Throw<DatabaseSchemaMigrationException>(
            () => SchemaMigrationPlanner.Plan(current, desired));

        exception.Message.ShouldContain("AlterTable");
    }

    private static CompiledSchema CompileOrders(bool linesFirst = false)
    {
        return DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("orders-db", database =>
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
            database.Trigger<Order>(TriggerEvent.AfterInsert, (transaction, row) => transaction.Audit("order.placed", row.Id));
            database.Principal("reader", principal => principal.Grant(Permission.Read, "orders", "order_lines"));
            database.Extension("sql.collation", "ordinal");
        }), EngineModel.Sql);
    }

    private static void AddOrders(IDatabaseSchemaBuilder database)
    {
        database.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.Column(order => order.Total);
            table.Index(order => order.CustomerId);
        });
    }

    private static void AddLines(IDatabaseSchemaBuilder database)
    {
        database.Table<OrderLine>("order_lines", table =>
        {
            table.Key(line => line.Id);
            table.References<Order>(line => line.OrderId);
            table.Column(line => line.Quantity);
            table.Column(line => line.UnitPrice);
        });
    }

    private static CompiledSchema CompileVersion(bool includeAge, bool allowDestructive = false)
        => DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("people", database =>
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
        }), EngineModel.Sql);

    private static CompiledSchema CompileIndex(bool useAge)
        => DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("people", database =>
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
            })), EngineModel.Sql);

    private static CompiledSchema CompileFunctionAndGrants(bool reverse)
        => DatabaseSchemaCompiler.Compile(DatabaseSchema.Create("orders-db", database =>
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<OrderLine>("order_lines", table => table.Key(line => line.Id));
            if (reverse)
            {
                database.Function("identity", (long renamed) => renamed);
                database.Principal("reader", principal =>
                {
                    principal.Grant(Permission.Read, "orders");
                    principal.Grant(Permission.Read, "order_lines");
                });
            }
            else
            {
                database.Function("identity", (long value) => value);
                database.Principal("reader", principal =>
                {
                    principal.Grant(Permission.Read, "order_lines");
                    principal.Grant(Permission.Read, "orders");
                });
            }
        }), EngineModel.Sql);

    private static CompiledSchema ReorderedSchema(string[] columnOrder, bool allowDestructive)
        => OrderedSchema(columnOrder, allowDestructive);

    private static CompiledSchema OrderedSchema(string[] columnOrder, bool allowDestructive)
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
        return new CompiledSchema(
            CompiledSchema.CurrentFormat,
            "people",
            EngineModel.Sql,
            allowDestructive,
            [],
            [new CompiledSchemaTable("People", "Tests:Person", columns, new CompiledSchemaKey("PK_People", ["Id"]), [], [])],
            [],
            [],
            [],
            [],
            []);
    }

    private static CompiledSchema CasedSchema(
        string tableName,
        string columnName,
        string keyName,
        string indexName)
        => new(
            CompiledSchema.CurrentFormat,
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
            [],
            []);

    private readonly record struct Money(decimal Amount);
    private sealed record Order(long Id, long CustomerId, Money Total);
    private sealed record OrderLine(long Id, long OrderId, int Quantity, Money UnitPrice);
    private sealed record InvalidOrderLine(long Id, string OrderId);
    private sealed record Person(long Id, string Name, int? Age);
    private sealed record SessionEntry(string Key, string Value, DateTimeOffset ExpiresAt);
}
