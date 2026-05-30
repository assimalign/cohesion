namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>
/// A top-level partial profile combining a scalar member, a nested complex member, and an
/// enumerable member. The source generator emits a <c>TryConfigureGenerated</c> override covering
/// all three, so it maps without <c>Expression.Compile()</c> at run time.
/// </summary>
public partial class GeneratedOrderProfile : MapperProfile<OrderTarget, OrderSource>
{
    protected override void Configure(MapperProfileDescriptor<OrderTarget, OrderSource> descriptor)
    {
        descriptor
            .MapMember(target => target.Id, source => source.Id)
            .MapMemberTypes(target => target.Customer!, source => source.Customer!)
            .MapMemberEnumerables(target => target.Items!, source => source.Items!);
    }
}

/// <summary>Nested-member element profile (generated).</summary>
public partial class GeneratedCustomerProfile : MapperProfile<CustomerTarget, CustomerSource>
{
    protected override void Configure(MapperProfileDescriptor<CustomerTarget, CustomerSource> descriptor)
    {
        descriptor
            .MapMember(target => target.Name, source => source.Name)
            .MapMember(target => target.Email, source => source.Email);
    }
}

/// <summary>Enumerable-element profile (generated).</summary>
public partial class GeneratedLineItemProfile : MapperProfile<LineItemTarget, LineItemSource>
{
    protected override void Configure(MapperProfileDescriptor<LineItemTarget, LineItemSource> descriptor)
    {
        descriptor
            .MapMember(target => target.Sku, source => source.Sku)
            .MapMember(target => target.Quantity, source => source.Quantity);
    }
}
