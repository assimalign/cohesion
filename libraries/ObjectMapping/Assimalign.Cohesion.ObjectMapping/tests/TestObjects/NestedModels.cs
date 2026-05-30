using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>Source aggregate with a nested complex member and an enumerable member.</summary>
public sealed class OrderSource
{
    public string? Id { get; set; }
    public CustomerSource? Customer { get; set; }
    public List<LineItemSource>? Items { get; set; }
}

/// <summary>Target aggregate with a nested complex member and an enumerable member.</summary>
public sealed class OrderTarget
{
    public string? Id { get; set; }
    public CustomerTarget? Customer { get; set; }
    public List<LineItemTarget>? Items { get; set; }
}

/// <summary>Nested source member type.</summary>
public sealed class CustomerSource
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

/// <summary>Nested target member type.</summary>
public sealed class CustomerTarget
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

/// <summary>Enumerable element source type.</summary>
public sealed class LineItemSource
{
    public string? Sku { get; set; }
    public int Quantity { get; set; }
}

/// <summary>Enumerable element target type.</summary>
public sealed class LineItemTarget
{
    public string? Sku { get; set; }
    public int Quantity { get; set; }
}
