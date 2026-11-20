namespace Assimalign.Cohesion.JsonSchema.Generation.Tests.Catalog;

/// <summary>A catalog item, named like <see cref="Inventory.Item"/> so that both need a definition name.</summary>
public sealed class Item
{
    public string Sku { get; set; } = string.Empty;
}
