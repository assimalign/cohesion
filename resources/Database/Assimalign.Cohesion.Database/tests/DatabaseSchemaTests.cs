using System;
using System.Linq.Expressions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public class DatabaseSchemaTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Schema: declarations retain the complete compile-time model")]
    public void Create_WithSchemaDeclarations_ShouldRetainCompileTimeModel()
    {
        IDatabaseSchema schema = DatabaseSchema.Create("orders", database =>
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
            database.Function("order_identity", (long orderId) => orderId);
            database.Trigger<Order>(
                TriggerEvent.AfterInsert,
                (transaction, row) => transaction.Audit("order.placed", row.Id));
            database.Principal(
                "appa-api",
                principal => principal.Grant(Permission.ReadWrite, "Orders", "OrderLines"));
        });

        schema.Name.ShouldBe("orders");

        IDatabaseSchemaType type = schema.Types.ShouldHaveSingleItem();
        type.ClrType.ShouldBe(typeof(Money));
        type.Precision.ShouldBe(18);
        type.Scale.ShouldBe(2);

        schema.Tables.Count.ShouldBe(2);
        IDatabaseSchemaTable order = schema.Tables[0];
        order.RowType.ShouldBe(typeof(Order));
        order.PrimaryKey.ShouldBe(nameof(Order.Id));
        order.Indexes.ShouldBe([nameof(Order.CustomerId)]);
        order.Columns.ShouldBe([nameof(Order.Id), nameof(Order.CustomerId)]);

        IDatabaseSchemaTable line = schema.Tables[1];
        line.RowType.ShouldBe(typeof(OrderLine));
        line.PrimaryKey.ShouldBe(nameof(OrderLine.Id));
        IDatabaseSchemaReference reference = line.References.ShouldHaveSingleItem();
        reference.Member.ShouldBe(nameof(OrderLine.OrderId));
        reference.TargetType.ShouldBe(typeof(Order));

        IDatabaseSchemaFunction function = schema.Functions.ShouldHaveSingleItem();
        function.Name.ShouldBe("order_identity");
        function.Body.ShouldBeAssignableTo<Expression<Func<long, long>>>();
        function.Body.Parameters.ShouldHaveSingleItem().Type.ShouldBe(typeof(long));

        IDatabaseSchemaTrigger trigger = schema.Triggers.ShouldHaveSingleItem();
        trigger.RowType.ShouldBe(typeof(Order));
        trigger.Event.ShouldBe(TriggerEvent.AfterInsert);
        trigger.Body.Parameters.Count.ShouldBe(2);
        trigger.Body.Parameters[0].Type.ShouldBe(typeof(IDatabaseTriggerContext));
        trigger.Body.Parameters[1].Type.ShouldBe(typeof(Order));

        IDatabaseSchemaPrincipal principal = schema.Principals.ShouldHaveSingleItem();
        principal.Name.ShouldBe("appa-api");
        IDatabaseSchemaGrant grant = principal.Grants.ShouldHaveSingleItem();
        grant.Permission.ShouldBe(Permission.ReadWrite);
        grant.Objects.ShouldBe(["Orders", "OrderLines"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: completed declarations are immutable snapshots")]
    public void Create_WhenRetainedBuildersChange_ShouldKeepCompletedSnapshot()
    {
        IDatabaseTableBuilder<Order>? retainedTable = null;
        IDatabasePrincipalBuilder? retainedPrincipal = null;
        IDatabaseSchema schema = DatabaseSchema.Create("orders", database =>
        {
            database.Table<Order>(table =>
            {
                retainedTable = table;
                table.Key(order => order.Id);
            });
            database.Principal("reader", principal =>
            {
                retainedPrincipal = principal;
                principal.Grant(Permission.Read, "Orders");
            });
        });

        retainedTable!.Index(order => order.CustomerId);
        retainedPrincipal!.Grant(Permission.Write, "Orders");

        schema.Tables.ShouldHaveSingleItem().Indexes.ShouldBeEmpty();
        schema.Principals.ShouldHaveSingleItem().Grants.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: table selectors require a direct row member")]
    public void Create_WithNestedOrStaticSelector_ShouldRejectSelector()
    {
        Should.Throw<ArgumentException>(() => DatabaseSchema.Create(
            "nested",
            database => database.Table<NestedOrder>(table => table.Column(order => order.Customer.Id))));

        Should.Throw<ArgumentException>(() => DatabaseSchema.Create(
            "static",
            database => database.Table<Order>(table => table.Column(_ => StaticId))));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Schema: invalid arguments are rejected at declaration time")]
    public void Create_WithInvalidArguments_ShouldRejectDeclaration()
    {
        Should.Throw<ArgumentNullException>(() => DatabaseSchema.Create(null!, _ => { }));
        Should.Throw<ArgumentException>(() => DatabaseSchema.Create(" ", _ => { }));
        Should.Throw<ArgumentNullException>(() => DatabaseSchema.Create("orders", null!));
        Should.Throw<ArgumentOutOfRangeException>(() => DatabaseSchema.Create(
            "orders",
            database => database.Type<Money>(type => type.Decimal(2, 3))));
        Should.Throw<ArgumentException>(() => DatabaseSchema.Create(
            "orders",
            database => database.Principal("reader", principal => principal.Grant(Permission.Read))));
    }

    private static long StaticId => 42;

    private readonly record struct Money(decimal Amount);

    private sealed record Order(long Id, long CustomerId, Money Total);

    private sealed record OrderLine(long Id, long OrderId, int Quantity, Money UnitPrice);

    private sealed record Customer(long Id);

    private sealed record NestedOrder(long Id, Customer Customer);
}
