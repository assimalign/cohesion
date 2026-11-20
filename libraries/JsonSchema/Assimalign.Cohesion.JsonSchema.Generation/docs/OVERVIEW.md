# Assimalign.Cohesion.JsonSchema.Generation

## Summary

Generates a JSON Schema document from a `System.Text.Json` contract: the `JsonTypeInfo` of a type,
including one from a source-generated `JsonSerializerContext`. The document describes exactly the
JSON that contract reads and writes, so a value serialized with the contract validates against it,
and it comes back as a loaded `JsonSchemaDocument` of the root
[Assimalign.Cohesion.JsonSchema](../../Assimalign.Cohesion.JsonSchema/docs/OVERVIEW.md) package,
ready to evaluate.

- Dialects: 2020-12 (the default), 2019-09 and draft-07.
- Types used in more than one place, or in their own definition, move into `$defs` (`definitions`
  in draft-07) under stable names taken from the type; types used once stay inline.
- Nullability, defaults, required members, naming policies, number handling, string enums,
  extension data, unmapped-member handling and `JsonDerivedType` polymorphism come from the
  contract.
- A caller hook sees every generated node with the contract it describes, and may change or
  replace it.
- NativeAOT- and trimming-safe: generation reads contract metadata only.

## Usage

```csharp
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Assimalign.Cohesion.JsonSchema;

public sealed class Order
{
    public required Address Shipping { get; init; }
    public Address? Billing { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

public sealed class Address
{
    public string Street { get; set; } = "";
}

public sealed class OrderLine
{
    public string Sku { get; set; } = "";
}

[JsonSerializable(typeof(Order))]
internal sealed partial class AppJsonContext : JsonSerializerContext { }
```

```csharp
JsonSchemaDocument schema = JsonSchemaDocument.Generate(AppJsonContext.Default.Order);

JsonElement order = JsonSerializer.SerializeToElement(
    new Order { Shipping = new Address { Street = "1 Main St" } },
    AppJsonContext.Default.Order);

bool valid = schema.IsValid(order);   // true: what the contract writes conforms
```

The generated 2020-12 document defines `Address` once, because two properties use it, refers to it
from both, and allows `null` only where the contract does:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": ["object", "null"],
  "properties": {
    "Shipping": { "$ref": "#/$defs/Address" },
    "Billing": { "anyOf": [{ "$ref": "#/$defs/Address" }, { "type": "null" }] },
    "Lines": {
      "type": "array",
      "items": { "type": ["object", "null"], "properties": { "Sku": { "type": "string" } } }
    }
  },
  "required": ["Shipping"],
  "$defs": {
    "Address": { "type": "object", "properties": { "Street": { "type": "string" } } }
  }
}
```

The root and the list items allow `null` because `System.Text.Json` writes `null` for a null
reference there and the contract does not say otherwise; `TreatNullObliviousAsNonNullable` makes
them strict.

Options choose the dialect and an identifier, and a transform adds what the contract cannot say.
The last argument loads the document, here into a shared registry with meta-schema validation:

```csharp
JsonSchemaRegistry registry = new();

JsonSchemaDocument schema = JsonSchemaDocument.Generate(
    AppJsonContext.Default.Order,
    new JsonSchemaGenerationOptions
    {
        Dialect = JsonSchemaDialect.Draft07,
        Id = new Uri("https://example.com/schemas/order"),
        TransformSchemaNode = (context, node) =>
        {
            if (context.TypeInfo.Type == typeof(Address) && !context.IsReference && node is JsonObject address)
            {
                address["description"] = "A postal address.";
            }

            return node;
        },
    },
    new JsonSchemaDocumentOptions { Registry = registry, ValidateSchema = true });
```

## Public surface

| Type | Role |
| --- | --- |
| `JsonSchemaDocument.Generate` | A static extension member on the root's `JsonSchemaDocument` (`JsonSchemaGenerationExtensions`): generates and loads the document. |
| `JsonSchemaGenerationOptions` | The dialect, an optional `$id`, `TreatNullObliviousAsNonNullable`, and `TransformSchemaNode`. |
| `IJsonSchemaGenerationContext` | What the transform receives with each node: the contract, the property, the polymorphic base type, and whether the node refers to a definition. |

The package shares the root's namespace, so `JsonSchemaDocument.Generate` needs no extra `using`.

## Dependencies

- `Assimalign.Cohesion.JsonSchema`, the root of the family.
- `System.Text.Json` (`JsonSchemaExporter`) and the rest of the BCL.

## Status

Complete for the three dialects
([#1904](https://github.com/assimalign/cohesion/issues/1904)). What the contract cannot express,
and the reasons behind each rule above, are in [DESIGN.md](DESIGN.md).
