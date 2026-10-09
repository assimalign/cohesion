using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Schema.Tests;

public class SqlSchemaTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Schema: one-step compilation matches the explicit compiler")]
    public void Compile_WithValidDeclaration_ShouldMatchTwoStepCompilation()
    {
        static void Configure(SqlSchemaBuilder database)
        {
            database.Type<Money>(type => type.Decimal(18, 2));
            database.Table<Order>("orders", table =>
            {
                table.Key(order => order.Id);
                table.Column(order => order.Total);
                table.Index(order => order.CustomerId);
            });
        }

        SqlCompiledSchema schema = SqlSchema.Compile("orders", Configure);
        SqlCompiledSchema twoStep = SqlSchemaCompiler.Compile(SqlSchema.Create("orders", Configure));

        schema.Hash.ShouldBe(twoStep.Hash);
        schema.CanonicalDocument.ShouldBe(twoStep.CanonicalDocument);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: one-step compilation preserves compiler validation errors")]
    public void Compile_WithInvalidDeclaration_ShouldMatchTwoStepValidationErrors()
    {
        static void Configure(SqlSchemaBuilder database)
        {
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Table<Order>("orders", table => table.Key(order => order.Id));
            database.Principal("reader", principal => principal.Grant(SqlPermission.Read, "missing"));
        }

        SqlSchemaValidationException exception = Should.Throw<SqlSchemaValidationException>(
            () => SqlSchema.Compile("invalid", Configure));
        SqlSchemaValidationException twoStep = Should.Throw<SqlSchemaValidationException>(
            () => SqlSchemaCompiler.Compile(SqlSchema.Create("invalid", Configure)));

        exception.Message.ShouldBe(twoStep.Message);
        exception.Errors
            .Select(error => (error.Code, error.Declaration, error.Message))
            .ShouldBe(twoStep.Errors.Select(error => (error.Code, error.Declaration, error.Message)));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: one-step compilation rejects null and empty arguments")]
    public void Compile_WithInvalidArguments_ShouldRejectDeclaration()
    {
        Should.Throw<ArgumentNullException>(() => SqlSchema.Compile(null!, _ => { }))
            .ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => SqlSchema.Compile(string.Empty, _ => { }))
            .ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => SqlSchema.Compile(" ", _ => { }))
            .ParamName.ShouldBe("name");
        Should.Throw<ArgumentNullException>(() => SqlSchema.Compile("orders", null!))
            .ParamName.ShouldBe("configure");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: declarations retain the complete compile-time model")]
    public void Create_WithSchemaDeclarations_ShouldRetainCompileTimeModel()
    {
        SqlSchema schema = SqlSchema.Create("orders", database =>
        {
            database.Type<Money>(type => type.Decimal(18, 2));
            database.Table<Order>(table =>
            {
                table.Key(order => order.Id);
                table.Index(order => order.CustomerId);
            });
            database.Table<OrderLine>(table =>
            {
                table.Key(line => line.Id);
                table.References<Order>(line => line.OrderId);
            });
            database.Principal(
                "appa-api",
                principal => principal.Grant(SqlPermission.ReadWrite, "Orders", "OrderLines"));
        });

        schema.Name.ShouldBe("orders");

        SqlSchemaType type = schema.Declaration.Types.ShouldHaveSingleItem();
        type.ClrType.ShouldBe(typeof(Money));
        type.Precision.ShouldBe(18);
        type.Scale.ShouldBe(2);

        schema.Declaration.Tables.Count.ShouldBe(2);
        SqlSchemaTable order = schema.Declaration.Tables[0];
        order.RowType.ShouldBe(typeof(Order));
        order.PrimaryKey.ShouldBe(nameof(Order.Id));
        order.Indexes.ShouldBe([nameof(Order.CustomerId)]);
        order.Columns.ShouldBe([nameof(Order.Id), nameof(Order.CustomerId)]);

        SqlSchemaTable line = schema.Declaration.Tables[1];
        line.RowType.ShouldBe(typeof(OrderLine));
        line.PrimaryKey.ShouldBe(nameof(OrderLine.Id));
        SqlSchemaReference reference = line.References.ShouldHaveSingleItem();
        reference.Member.ShouldBe(nameof(OrderLine.OrderId));
        reference.TargetType.ShouldBe(typeof(Order));

        SqlSchemaPrincipal principal = schema.Declaration.Principals.ShouldHaveSingleItem();
        principal.Name.ShouldBe("appa-api");
        SqlSchemaGrant grant = principal.Grants.ShouldHaveSingleItem();
        grant.Permission.ShouldBe(SqlPermission.ReadWrite);
        grant.Objects.ShouldBe(["Orders", "OrderLines"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: completed declarations are immutable snapshots")]
    public void Create_WhenRetainedBuildersChange_ShouldKeepCompletedSnapshot()
    {
        SqlTableBuilder<Order>? retainedTable = null;
        SqlPrincipalBuilder? retainedPrincipal = null;
        SqlSchema schema = SqlSchema.Create("orders", database =>
        {
            database.Table<Order>(table =>
            {
                retainedTable = table;
                table.Key(order => order.Id);
            });
            database.Principal("reader", principal =>
            {
                retainedPrincipal = principal;
                principal.Grant(SqlPermission.Read, "Orders");
            });
        });

        retainedTable!.Index(order => order.CustomerId);
        retainedPrincipal!.Grant(SqlPermission.Write, "Orders");

        schema.Declaration.Tables.ShouldHaveSingleItem().Indexes.ShouldBeEmpty();
        schema.Declaration.Principals.ShouldHaveSingleItem().Grants.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: table selectors require a direct row member")]
    public void Create_WithNestedOrStaticSelector_ShouldRejectSelector()
    {
        Should.Throw<ArgumentException>(() => SqlSchema.Create(
            "nested",
            database => database.Table<NestedOrder>(table => table.Column(order => order.Customer.Id))));

        Should.Throw<ArgumentException>(() => SqlSchema.Create(
            "static",
            database => database.Table<Order>(table => table.Column(_ => StaticId))));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: invalid arguments are rejected at declaration time")]
    public void Create_WithInvalidArguments_ShouldRejectDeclaration()
    {
        Should.Throw<ArgumentNullException>(() => SqlSchema.Create(null!, _ => { }));
        Should.Throw<ArgumentException>(() => SqlSchema.Create(" ", _ => { }));
        Should.Throw<ArgumentNullException>(() => SqlSchema.Create("orders", null!));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlSchema.Create(
            "orders",
            database => database.Type<Money>(type => type.Decimal(2, 3))));
        Should.Throw<ArgumentException>(() => SqlSchema.Create(
            "orders",
            database => database.Principal("reader", principal => principal.Grant(SqlPermission.Read))));

        // The sealed builder's parameters carry the names its documentation gives; the former
        // implementation behind the interface reported "tableName".
        Should.Throw<ArgumentException>(() => SqlSchema.Create(
            "orders",
            database => database.Table<Order>(" ", _ => { }))).ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: a declaration compiles itself as the one-step form does")]
    public void Compile_FromDeclaration_ShouldMatchOneStepCompilation()
    {
        static void Configure(SqlSchemaBuilder database)
        {
            database.Table<Order>("orders", table =>
            {
                table.PrimaryKey(order => order.Id);
                table.Index(order => order.CustomerId);
            });
        }

        SqlSchema declaration = SqlSchema.Create("orders", Configure);
        SqlCompiledSchema compiled = declaration.Compile();

        declaration.Name.ShouldBe("orders");
        compiled.Name.ShouldBe("orders");
        compiled.Format.ShouldBe(SqlCompiledSchema.CurrentFormat);
        compiled.Hash.ShouldBe(SqlSchema.Compile("orders", Configure).Hash);
    }

    private static long StaticId => 42;

    private readonly record struct Money(decimal Amount);

    private sealed record Order(long Id, long CustomerId, Money Total);

    private sealed record OrderLine(long Id, long OrderId, int Quantity, Money UnitPrice);

    private sealed record Customer(long Id);

    private sealed record NestedOrder(long Id, Customer Customer);
}
