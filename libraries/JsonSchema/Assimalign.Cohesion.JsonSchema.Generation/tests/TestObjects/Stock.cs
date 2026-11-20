namespace Assimalign.Cohesion.JsonSchema.Generation.Tests;

/// <summary>Uses two types that share a name, each twice.</summary>
public sealed class Stock
{
    public Catalog.Item? Listed { get; set; }

    public Catalog.Item? Featured { get; set; }

    public Inventory.Item? OnHand { get; set; }

    public Inventory.Item? Reserved { get; set; }
}
