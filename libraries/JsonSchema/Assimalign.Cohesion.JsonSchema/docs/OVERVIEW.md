# Assimalign.Cohesion.JsonSchema

## Summary

The root of the Cohesion JSON Schema family: a JSON Schema evaluator for every released dialect,
built on `System.Text.Json` and nothing else. It loads schema documents, resolves `$ref`,
`$dynamicRef` and `$recursiveRef` within and across documents, and evaluates JSON instances with
the specification's semantics and output formats.

| Dialect | `$schema` | Notes |
| --- | --- | --- |
| Draft 4 | `http://json-schema.org/draft-04/schema#` | `id`, boolean `exclusiveMinimum`/`exclusiveMaximum` |
| Draft 6 | `http://json-schema.org/draft-06/schema#` | |
| Draft 7 | `http://json-schema.org/draft-07/schema#` | `if`/`then`/`else`, `$comment`, content keywords |
| 2019-09 | `https://json-schema.org/draft/2019-09/schema` | vocabularies, `$recursiveRef`, `unevaluated*` |
| 2020-12 | `https://json-schema.org/draft/2020-12/schema` | the default; `$dynamicRef`, `prefixItems` |

Conformance is measured by the official
[JSON Schema Test Suite](https://github.com/json-schema-org/JSON-Schema-Test-Suite), vendored in
`tests/TestObjects/JSON-Schema-Test-Suite`: every required and optional validation test and every
annotation test passes in every dialect, with no exclusions ([PROVENANCE.md](PROVENANCE.md)).

## Usage

```csharp
using System.Text.Json;
using Assimalign.Cohesion.JsonSchema;

JsonSchemaDocument schema = JsonSchemaDocument.Parse("""
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "properties": { "email": { "type": "string", "format": "email" } },
      "required": [ "email" ]
    }
    """);

using JsonDocument instance = JsonDocument.Parse("""{ "email": "not-an-address" }""");

bool valid = schema.IsValid(instance.RootElement);   // true: format annotates by default

JsonSchemaEvaluationResult result = schema.Evaluate(instance.RootElement, new JsonSchemaEvaluationOptions
{
    FormatAssertion = true,
    OutputFormat = JsonSchemaOutputFormat.Basic,
});

foreach (JsonSchemaEvaluationResult error in result.Errors)
{
    Console.WriteLine($"{error.InstanceLocation}: {error.Error}");   // /email: ...
}
```

Documents that refer to each other share a `JsonSchemaRegistry`. A registry resolves a reference
it does not hold through its `IJsonSchemaResolver`, and always serves the official meta-schemas
without one:

```csharp
JsonSchemaRegistry registry = new() { Resolver = myResolver };
JsonSchemaDocumentOptions options = new() { Registry = registry, ValidateSchema = true };

JsonSchemaDocument address = JsonSchemaDocument.Parse(addressJson, options);   // "$id": "https://example.com/address"
JsonSchemaDocument order = JsonSchemaDocument.Parse(orderJson, options);       // "$ref": "address"
```

## Public surface

| Type | Role |
| --- | --- |
| `JsonSchemaDocument` | A loaded, compiled document: `Parse`, `ParseAsync`, `Create`, `Evaluate`, `IsValid`, `TryGetSchema`. Immutable and thread-safe once loaded. |
| `IJsonSchema` | One schema of a document (the root or any subschema), with its base URI, location, dialect and JSON. |
| `JsonSchemaRegistry` | The resources, dialects and vocabularies references resolve against; thread-safe, atomic registration. |
| `IJsonSchemaResolver` | Supplies documents the registry does not hold. The library never reaches the network itself. |
| `JsonSchemaDocumentOptions` | Load options: base URI, default dialect, registry, regular-expression timeout, meta-schema validation. |
| `JsonSchemaEvaluationOptions` | Evaluation options: output format, format and content assertion, maximum depth, custom formats. |
| `JsonSchemaEvaluationResult` | The result in Flag, Basic, Detailed or Verbose form (2020-12 Core §12), with `WriteTo` for the JSON form. |
| `JsonSchemaDialect`, `JsonSchemaVocabulary` | The five built-in dialects and their vocabularies; `Create` builds custom ones. |
| `JsonSchemaKeywordDefinition`, `IJsonSchemaKeyword`, `IJsonSchemaKeywordContext` | Custom keywords: a factory per occurrence, evaluated with the instance, subschemas, annotations and evaluated positions. |
| `IJsonSchemaFormat`, `JsonSchemaFormat` | Custom formats, or replacements for built-in ones. |
| `JsonPointer` | An RFC 6901 JSON Pointer value object, used for every location in results and lookups. |
| `JsonSchemaException`, `JsonSchemaErrorCode` | Schemas the library cannot process. A failed validation is a result, never an exception. |

## Dependencies

- `System.Text.Json` and the rest of the BCL. No other package, and no Cohesion library.

The rest of the family builds on this package and never the reverse; see [DESIGN.md](DESIGN.md),
"Family map".

## Status

Complete for the five released dialects, including every format 2020-12 defines
(`idn-hostname` and `idn-email` with IDNA2008 and the UTS #46 mapping) and content assertion. The
planned family members are tracked under epic
[#1901](https://github.com/assimalign/cohesion/issues/1901).
