using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

/// <summary>
/// Domains in the typed schema (phase E3): <c>schema.Type&lt;T&gt;</c> lowers to a domain named for
/// <c>T</c>, <c>table.Column(selector, sqlType)</c> declares a column of a named SQL type, the document
/// format is <c>v3</c>, and the migration planner plans adding and dropping a domain.
/// </summary>
public sealed class CompiledSchemaDomainTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Schema CHAR: fixed length has its own identity and changing to VARCHAR requires a migration")]
    [InlineData("CHAR(3)", 3)]
    [InlineData("CHARACTER", 1)]
    public void Compile_CharacterColumn_ShouldPersistIdentityAndPlanTypeChange(string spelling, int length)
    {
        // Arrange
        SqlCompiledSchema before = SqlSchema.Compile("crm", schema => schema.Table<Customer>("customers", table => table.Column(customer => customer.Code, spelling)));
        SqlCompiledSchema after = SqlSchema.Compile("crm", schema =>
        {
            schema.AllowDestructiveChanges();
            schema.Table<Customer>("customers", table => table.Column(customer => customer.Code, "VARCHAR(3)"));
        });
        // Act
        var restored = SqlCompiledSchemaSerializer.Deserialize(before.CanonicalDocument);
        var operation = SqlSchemaMigrationPlanner.Plan(before, after).Operations.ShouldHaveSingleItem();
        // Assert
        restored.Tables[0].Columns[0].Type.ShouldBe(DatabaseType.Character);
        restored.Tables[0].Columns[0].MaxLength.ShouldBe(length);
        operation.Kind.ShouldBe(SqlSchemaMigrationOperationKind.AlterColumn);
        operation.Safety.ShouldBe(SqlSchemaMigrationSafety.Destructive);
        after.Hash.ShouldNotBe(before.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema domains: schema.Type<T> lowers to a domain named for T, and its members' columns are declared with it")]
    public void Compile_TypeDeclaration_ShouldLowerToANamedDomain()
    {
        // Act
        SqlCompiledSchema schema = SqlSchema.Compile("shop", database =>
        {
            database.Type<Money>(type => type.Decimal(18, 2));
            database.Table<Product>("products", table =>
            {
                table.Key(product => product.Id);
                table.Column(product => product.Price);
            });
        });

        // Assert
        schema.Format.ShouldBe("cohesion/database-schema/v11");
        schema.Types.ShouldHaveSingleItem().ShouldBe(new CompiledSchemaType("Money", DatabaseType.Decimal, 18, 2));
        schema.Tables[0].Columns[1].ShouldBe(new CompiledSchemaColumn("Price", DatabaseType.Decimal, false, Precision: 18, Scale: 2, CustomType: "Money"));
        SqlCompiledSchemaSerializer.Deserialize(schema.CanonicalDocument).Hash.ShouldBe(schema.Hash);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema domains: table.Column(selector, sqlType) keeps the type as written, after Key or before it")]
    public void Compile_ColumnWithSqlType_ShouldKeepTheNameAsWritten()
    {
        // Act: the email column's type is declared after its member was indexed, the code's before its key.
        SqlCompiledSchema schema = SqlSchema.Compile("crm", database => database.Table<Customer>("customers", table =>
        {
            table.Column(customer => customer.Code, "VARCHAR(16)");
            table.Key(customer => customer.Code);
            table.Index(customer => customer.Email);
            table.Column(customer => customer.Email, "email");
        }));

        // Assert: the engine resolves the names; the package keeps them and the CLR storage type.
        CompiledSchemaTable customers = schema.Tables.ShouldHaveSingleItem();
        customers.Columns.ShouldBe([
            new CompiledSchemaColumn("Code", DatabaseType.String, true, CustomType: "VARCHAR(16)"),
            new CompiledSchemaColumn("Email", DatabaseType.String, true, CustomType: "email"),
        ]);
        schema.Types.ShouldBeEmpty();
        SqlCompiledSchemaSerializer.Deserialize(schema.CanonicalDocument).Hash.ShouldBe(schema.Hash);
        SqlSchema.Compile("crm", database => database.Table<Customer>("customers", table =>
        {
            table.Column(customer => customer.Code, "VARCHAR(16)");
            table.Key(customer => customer.Code);
            table.Index(customer => customer.Email);
            table.Column(customer => customer.Email, "contact_email");
        })).Hash.ShouldNotBe(schema.Hash, "the column's type name is part of the document");
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Schema domains: table.Column(selector, sqlType) refuses a name that is not a type name")]
    [InlineData("email; DROP TABLE x")]
    [InlineData("1email")]
    [InlineData("VARCHAR(x)")]
    [InlineData("DECIMAL(18, 2, 1)")]
    [InlineData("\"email\"")]
    public void Column_WithMalformedSqlType_ShouldBeRefused(string sqlType)
        => Should.Throw<SqlSchemaValidationException>(() => SqlSchema.Compile("crm", database => database.Table<Customer>("customers", table =>
        {
            table.Key(customer => customer.Code);
            table.Column(customer => customer.Email, sqlType);
        }))).Errors.ShouldHaveSingleItem().Declaration.ShouldBe("customers.Email");

    [Fact(DisplayName = "Cohesion Test [Database] - Schema domains: a column declared with two SQL types is refused at the second")]
    public void Column_DeclaredWithTwoSqlTypes_ShouldBeRefused()
        => Should.Throw<ArgumentException>(() => SqlSchema.Compile("crm", database => database.Table<Customer>("customers", table =>
        {
            table.Key(customer => customer.Code);
            table.Column(customer => customer.Email, "email");
            table.Column(customer => customer.Email, "contact_email");
        }))).Message.ShouldContain("already declared with SQL type 'email'");

    [Fact(DisplayName = "Cohesion Test [Database] - Schema domains: the planner adds a domain before the tables that use it and drops one after them")]
    public void Plan_DomainAddedAndDropped_ShouldOrderAroundTheTables()
    {
        // Arrange
        SqlCompiledSchema empty = SqlSchema.Compile("shop", database =>
            database.Table<Order>("orders", table => table.Key(order => order.Id)));
        SqlCompiledSchema withPrice = SqlSchema.Compile("shop", database =>
        {
            database.Type<Money>(type => type.Decimal(18, 2));
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<Product>("products", table =>
            {
                table.Key(product => product.Id);
                table.Column(product => product.Price);
            });
        });
        SqlCompiledSchema widened = SqlSchema.Compile("shop", database =>
        {
            database.AllowDestructiveChanges();
            database.Type<Money>(type => type.Decimal(19, 4));
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<Product>("products", table =>
            {
                table.Key(product => product.Id);
                table.Column(product => product.Price);
            });
        });
        SqlCompiledSchema dropped = SqlSchema.Compile("shop", database =>
        {
            database.AllowDestructiveChanges();
            database.Table<Order>("orders", table => table.Key(order => order.Id));
        });

        // Act
        var added = SqlSchemaMigrationPlanner.Plan(empty, withPrice).Operations;
        var changed = SqlSchemaMigrationPlanner.Plan(withPrice, widened).Operations;
        var removed = SqlSchemaMigrationPlanner.Plan(withPrice, dropped).Operations;

        // Assert
        added.Select(operation => (operation.Kind, operation.ObjectName)).ShouldBe([
            (SqlSchemaMigrationOperationKind.AddDomain, "Money"),
            (SqlSchemaMigrationOperationKind.AddTable, "products"),
        ]);
        added[0].Safety.ShouldBe(SqlSchemaMigrationSafety.Safe);
        added[0].Domain.ShouldBe(new CompiledSchemaType("Money", DatabaseType.Decimal, 18, 2));
        changed.Select(operation => (operation.Kind, operation.ObjectName)).ShouldBe([
            (SqlSchemaMigrationOperationKind.DropDomain, "Money"),
            (SqlSchemaMigrationOperationKind.AddDomain, "Money"),
            (SqlSchemaMigrationOperationKind.AlterColumn, "Price"),
        ]);
        removed.Select(operation => (operation.Kind, operation.ObjectName)).ShouldBe([
            (SqlSchemaMigrationOperationKind.DropTable, "products"),
            (SqlSchemaMigrationOperationKind.DropDomain, "Money"),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema domains: a document's custom type must be a SQL identifier, and a column's type a type name")]
    public void Document_WithInvalidTypeNames_ShouldBeRefused()
    {
        // Act
        var exception = Should.Throw<SqlSchemaValidationException>(() => new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat,
            "shop",
            allowsDestructiveChanges: false,
            [new CompiledSchemaType("Acme:Money", DatabaseType.Decimal, 18, 2)],
            [new CompiledSchemaTable("t", "Tests:T", [new CompiledSchemaColumn("c", DatabaseType.String, true, CustomType: "x y")], null, [], [])],
            []));

        // Assert
        exception.Errors.Select(error => error.Code).ShouldBe([SqlSchemaValidationErrorCode.UnsupportedType, SqlSchemaValidationErrorCode.UnsupportedType],
            ignoreOrder: true);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Schema domains: a SQL type name is an identifier with up to two unsigned arguments")]
    [InlineData("email", true)]
    [InlineData("_money2", true)]
    [InlineData("VARCHAR(320)", true)]
    [InlineData("DECIMAL(18, 2)", true)]
    [InlineData("DECIMAL (18,2)", true)]
    [InlineData("", false)]
    [InlineData("2email", false)]
    [InlineData("e-mail", false)]
    [InlineData("VARCHAR(-1)", false)]
    [InlineData("VARCHAR()", false)]
    [InlineData("VARCHAR(1", false)]
    [InlineData("VARCHAR(1)x", false)]
    public void IsTypeName_ShouldAcceptOnlyTheShape(string text, bool expected)
        => CompiledSchemaTypeNames.IsTypeName(text).ShouldBe(expected);

    [Theory(DisplayName = "Cohesion Test [Database] - Schema domains: SERIAL names are refused before provisioning")]
    [InlineData("serial")][InlineData("bigserial")][InlineData("smallserial")]
    public void Document_SerialTypeName_ShouldRejectUnreachableDomain(string name)
    {
        // Arrange / Act
        var failure = Should.Throw<SqlSchemaValidationException>(() => new SqlCompiledSchema(
            SqlCompiledSchema.CurrentFormat, "shop", false, [new CompiledSchemaType(name, DatabaseType.Int32, null, null)], [], []));

        // Assert
        var error = failure.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(SqlSchemaValidationErrorCode.UnsupportedType);
        error.Message.ShouldContain("CK00001", Case.Sensitive);
        error.Message.ShouldContain("GENERATED BY DEFAULT AS IDENTITY", Case.Sensitive);
    }

    private readonly record struct Money(decimal Amount);
    private sealed record Product(long Id, Money Price);
    private sealed record Order(long Id);
    private sealed record Customer(string Code, string Email);
}
