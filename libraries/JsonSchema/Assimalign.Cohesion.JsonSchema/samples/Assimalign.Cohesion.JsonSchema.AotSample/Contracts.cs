using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.JsonSchema.AotSample;

/// <summary>A recursive, polymorphic contract the generator describes and the sample round-trips.</summary>
public sealed class Catalog
{
    public string Name { get; set; } = string.Empty;

    public List<Product> Products { get; set; } = new();

    public Category? Root { get; set; }

    public Category? Featured { get; set; }
}

/// <summary>A tree: a type that contains itself.</summary>
public sealed class Category
{
    public string Title { get; set; } = string.Empty;

    public List<Category> Children { get; set; } = new();
}

/// <summary>A polymorphic base with a string and a number discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Book), "book")]
[JsonDerivedType(typeof(Album), 2)]
public abstract class Product
{
    public string Sku { get; set; } = string.Empty;

    public decimal Price { get; set; }
}

public sealed class Book : Product
{
    public string? Isbn { get; set; }
}

public sealed class Album : Product
{
    public int Tracks { get; set; }

    public Format Format { get; set; }
}

public enum Format
{
    Vinyl,
    Compact,
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(Catalog))]
internal sealed partial class SampleContracts : JsonSerializerContext
{
}
