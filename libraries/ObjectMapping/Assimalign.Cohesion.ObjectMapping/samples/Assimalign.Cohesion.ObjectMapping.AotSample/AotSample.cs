using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.AotSample;

/// <summary>Source aggregate mapped by <see cref="OrderProfile"/>.</summary>
public sealed class OrderEntity
{
    public string? Id { get; set; }
    public CustomerEntity? Customer { get; set; }
    public List<LineEntity>? Lines { get; set; }
}

/// <summary>Target aggregate produced by <see cref="OrderProfile"/>.</summary>
public sealed class OrderDto
{
    public string? Id { get; set; }
    public CustomerDto? Customer { get; set; }
    public List<LineDto>? Lines { get; set; }
}

/// <summary>Nested source member.</summary>
public sealed class CustomerEntity
{
    public string? Name { get; set; }
}

/// <summary>Nested target member.</summary>
public sealed class CustomerDto
{
    public string? Name { get; set; }
}

/// <summary>Enumerable source element.</summary>
public sealed class LineEntity
{
    public string? Sku { get; set; }
    public int Quantity { get; set; }
}

/// <summary>Enumerable target element.</summary>
public sealed class LineDto
{
    public string? Sku { get; set; }
    public int Quantity { get; set; }
}

/// <summary>Combines scalar, nested, and enumerable mappings; covered by the source generator.</summary>
public partial class OrderProfile : MapperProfile<OrderDto, OrderEntity>
{
    protected override void Configure(MapperProfileDescriptor<OrderDto, OrderEntity> descriptor)
    {
        descriptor
            .MapMember(target => target.Id, source => source.Id)
            .MapMemberTypes(target => target.Customer!, source => source.Customer!)
            .MapMemberEnumerables(target => target.Lines!, source => source.Lines!);
    }
}

/// <summary>Nested-member profile; covered by the source generator.</summary>
public partial class CustomerProfile : MapperProfile<CustomerDto, CustomerEntity>
{
    protected override void Configure(MapperProfileDescriptor<CustomerDto, CustomerEntity> descriptor)
        => descriptor.MapMember(target => target.Name, source => source.Name);
}

/// <summary>Enumerable-element profile; covered by the source generator.</summary>
public partial class LineProfile : MapperProfile<LineDto, LineEntity>
{
    protected override void Configure(MapperProfileDescriptor<LineDto, LineEntity> descriptor)
        => descriptor
            .MapMember(target => target.Sku, source => source.Sku)
            .MapMember(target => target.Quantity, source => source.Quantity);
}

/// <summary>
/// Exercises the runtime map path so the trim/AOT analyzers see it. A clean build of this project
/// (with IL2026/IL3050 promoted to errors) proves the generated profiles are AOT-safe.
/// </summary>
public static class AotSample
{
    /// <summary>Maps an <see cref="OrderEntity"/> to an <see cref="OrderDto"/> via generated profiles.</summary>
    /// <param name="entity">The source entity.</param>
    /// <returns>The mapped DTO.</returns>
    public static OrderDto Map(OrderEntity entity)
    {
        IMapper mapper = new MapperBuilder()
            .AddProfile(new OrderProfile())
            .AddProfile(new CustomerProfile())
            .AddProfile(new LineProfile())
            .Build();

        // Map onto a pre-created target (no Activator on the hot path).
        return mapper.Map(new OrderDto(), entity);
    }

    /// <summary>
    /// Maps using inline <c>AddProfile&lt;T,S&gt;(lambda)</c> profiles. The generator intercepts these
    /// call sites (registering generated delegate-based profiles) and suppresses the unreachable
    /// expression configuration in this method, so it is AOT-safe despite the inline fluent syntax.
    /// </summary>
    /// <param name="entity">The source entity.</param>
    /// <returns>The mapped DTO.</returns>
    public static OrderDto MapInline(OrderEntity entity)
    {
        IMapper mapper = new MapperBuilder()
            .AddProfile<CustomerDto, CustomerEntity>(d => d
                .MapMember(t => t.Name, s => s.Name))
            .AddProfile<LineDto, LineEntity>(d => d
                .MapMember(t => t.Sku, s => s.Sku)
                .MapMember(t => t.Quantity, s => s.Quantity))
            .AddProfile<OrderDto, OrderEntity>(d => d
                .MapMember(t => t.Id, s => s.Id)
                .MapMemberTypes(t => t.Customer!, s => s.Customer!)
                .MapMemberEnumerables(t => t.Lines!, s => s.Lines!))
            .Build();

        return mapper.Map(new OrderDto(), entity);
    }
}
