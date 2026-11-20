using System;
using System.Buffers;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.AotSample;

/// <summary>
/// The NativeAOT guard for the JsonSchema family. Each check loads, evaluates or generates through a
/// different code path: dialects and meta-schema validation, <c>$ref</c>, <c>$dynamicRef</c> and remote
/// references, asserted formats (with idn-hostname's IDNA tables), regular expressions, a custom format
/// and keyword, every output format, and documents generated from a source-generated contract in the
/// three dialects the generator writes. It prints one line per check and exits 1 when any fails, so a
/// trimmed or AOT-compiled build that loses a code path fails the run, not only the build.
/// </summary>
internal static class Program
{
    private const string orderSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://example.com/order",
          "type": "object",
          "properties": {
            "id": { "type": "string", "format": "uuid" },
            "email": { "type": "string", "format": "email" },
            "host": { "type": "string", "format": "idn-hostname" },
            "placed": { "type": "string", "format": "date-time" },
            "code": { "type": "string", "pattern": "^[A-Z]{3}-\\d+$" },
            "lines": { "type": "array", "items": { "$ref": "#/$defs/line" }, "minItems": 1 },
            "address": { "$ref": "https://example.com/address" }
          },
          "required": [ "id", "lines" ],
          "unevaluatedProperties": false,
          "$defs": {
            "line": {
              "type": "object",
              "properties": { "sku": { "type": "string" }, "qty": { "type": "integer", "minimum": 1 } },
              "required": [ "sku", "qty" ]
            }
          }
        }
        """;

    private const string addressSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": { "street": { "type": "string" } },
          "required": [ "street" ]
        }
        """;

    private const string validOrder = """
        {
          "id": "8a0f8d5e-6a64-4b5e-9f0d-3c2a1b0e9d7c",
          "email": "orders@example.com",
          "host": "例え.テスト",
          "placed": "2026-11-20T08:30:00-05:00",
          "code": "ABC-42",
          "lines": [ { "sku": "A-1", "qty": 2 } ],
          "address": { "street": "1 Main St" }
        }
        """;

    private const string treeSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://example.com/tree",
          "$dynamicAnchor": "node",
          "type": "object",
          "properties": {
            "data": true,
            "children": { "type": "array", "items": { "$dynamicRef": "#node" } }
          }
        }
        """;

    private const string strictTreeSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://example.com/strict-tree",
          "$dynamicAnchor": "node",
          "$ref": "tree",
          "unevaluatedProperties": false
        }
        """;

    private static int _failures;

    public static int Main()
    {
        CheckReferencesAndFormats();
        CheckDynamicReferences();
        CheckMetaSchemaValidation();
        CheckCustomFormatAndKeyword();
        CheckOutputFormats();
        CheckGeneration();

        Console.WriteLine(_failures == 0 ? "JsonSchema AOT sample: all checks passed." : "JsonSchema AOT sample: " + _failures + " check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckReferencesAndFormats()
    {
        JsonSchemaRegistry registry = new()
        {
            Resolver = new StaticResolver().Add("https://example.com/address", addressSchema),
        };
        JsonSchemaDocument document = JsonSchemaDocument.Parse(orderSchema, new JsonSchemaDocumentOptions { Registry = registry });
        JsonSchemaEvaluationOptions formats = new() { FormatAssertion = true };

        Check("2020-12: a valid order", document.IsValid(Parse(validOrder), formats));
        Check("2020-12: a remote reference is followed", !document.IsValid(Parse(validOrder.Replace("\"street\"", "\"road\"", StringComparison.Ordinal)), formats));
        Check("2020-12: unevaluatedProperties rejects an unknown member", !document.IsValid(Parse(validOrder.Replace("\"code\"", "\"coupon\"", StringComparison.Ordinal)), formats));
        Check("2020-12: a pattern is matched", !document.IsValid(Parse(validOrder.Replace("ABC-42", "abc-42", StringComparison.Ordinal)), formats));
        Check("format: idn-hostname rejects an invalid A-label", !document.IsValid(Parse(validOrder.Replace("例え.テスト", "xn--a.example", StringComparison.Ordinal)), formats));
        Check("format: date-time rejects a time without an offset", !document.IsValid(Parse(validOrder.Replace("-05:00", string.Empty, StringComparison.Ordinal)), formats));
        Check("format: an annotation unless asserted", document.IsValid(Parse(validOrder.Replace("-05:00", string.Empty, StringComparison.Ordinal))));
    }

    private static void CheckDynamicReferences()
    {
        JsonSchemaRegistry registry = new();
        JsonSchemaDocumentOptions options = new() { Registry = registry };
        JsonSchemaDocument tree = JsonSchemaDocument.Parse(treeSchema, options);
        JsonSchemaDocument strict = JsonSchemaDocument.Parse(strictTreeSchema, options);
        JsonElement misspelled = Parse("""{ "children": [ { "daat": 1 } ] }""");

        Check("$dynamicRef: the base tree accepts any member", tree.IsValid(misspelled));
        Check("$dynamicRef: the extended tree applies to nested nodes", !strict.IsValid(misspelled));
    }

    private static void CheckMetaSchemaValidation()
    {
        JsonSchemaDocumentOptions validate = new() { ValidateSchema = true };
        JsonSchemaDocument draft07 = JsonSchemaDocument.Parse("""
            { "$schema": "http://json-schema.org/draft-07/schema#", "type": "array", "items": { "type": "integer" }, "maxItems": 3 }
            """, validate);

        Check("draft-07: items and maxItems", draft07.IsValid(Parse("[1, 2]")) && !draft07.IsValid(Parse("[1, 2, 3, 4]")));

        // A non-string title compiles, since title is an annotation, so only the meta-schema refuses it.
        const string nonConforming = """{ "$schema": "https://json-schema.org/draft/2020-12/schema", "title": 5 }""";
        bool loadsWithoutValidation = JsonSchemaDocument.Parse(nonConforming).IsValid(Parse("1"));
        bool refused;
        try
        {
            JsonSchemaDocument.Parse(nonConforming, validate);
            refused = false;
        }
        catch (JsonSchemaException exception)
        {
            refused = exception.Code == JsonSchemaErrorCode.MetaSchemaViolation;
        }

        Check("meta-schema: a non-conforming schema is refused only when validation is on", loadsWithoutValidation && refused);
    }

    private static void CheckCustomFormatAndKeyword()
    {
        JsonSchemaEvaluationOptions evenLength = new()
        {
            FormatAssertion = true,
            Formats = [JsonSchemaFormat.Create("even-length", value => value.Length % 2 == 0)],
        };
        JsonSchemaDocument formatted = JsonSchemaDocument.Parse("""{ "format": "even-length" }""");
        Check("custom format: asserted", formatted.IsValid(Parse("\"ab\""), evenLength) && !formatted.IsValid(Parse("\"abc\""), evenLength));

        JsonSchemaVocabulary vocabulary = JsonSchemaVocabulary.Create(
            new Uri("https://example.com/vocab/even"),
            [JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword())]);
        JsonSchemaDialect dialect = JsonSchemaDialect.Create(new Uri("https://example.com/dialect/even"),
        [
            JsonSchemaVocabulary.Draft202012Core,
            JsonSchemaVocabulary.Draft202012Applicator,
            JsonSchemaVocabulary.Draft202012Validation,
            vocabulary,
        ]);
        JsonSchemaDocument keyword = JsonSchemaDocument.Parse("""{ "items": { "x-even": true } }""", new JsonSchemaDocumentOptions { DefaultDialect = dialect });
        Check("custom keyword: evaluated", keyword.IsValid(Parse("[2, 4]")) && !keyword.IsValid(Parse("[2, 3]")));
    }

    private static void CheckOutputFormats()
    {
        JsonSchemaDocument document = JsonSchemaDocument.Parse("""{ "properties": { "n": { "type": "integer", "minimum": 10 } } }""");
        JsonElement instance = Parse("""{ "n": 3 }""");

        foreach (JsonSchemaOutputFormat format in (JsonSchemaOutputFormat[])[JsonSchemaOutputFormat.Flag, JsonSchemaOutputFormat.Basic, JsonSchemaOutputFormat.Detailed, JsonSchemaOutputFormat.Verbose])
        {
            JsonSchemaEvaluationResult result = document.Evaluate(instance, new JsonSchemaEvaluationOptions { OutputFormat = format });
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                result.WriteTo(writer);
            }

            Check("output: " + format, !result.IsValid && buffer.WrittenCount > 0 && (format == JsonSchemaOutputFormat.Flag || result.ToString().Contains("minimum", StringComparison.Ordinal)));
        }
    }

    private static void CheckGeneration()
    {
        Catalog catalog = new()
        {
            Name = "Spring",
            Products =
            {
                new Book { Sku = "B-1", Price = 12.5m, Isbn = "978-0131103627" },
                new Album { Sku = "A-1", Price = 20m, Tracks = 11, Format = Format.Vinyl },
            },
            Root = new Category { Title = "All", Children = { new Category { Title = "Books" }, new Category { Title = "Music" } } },
        };
        JsonElement instance = JsonSerializer.SerializeToElement(catalog, SampleContracts.Default.Catalog);
        JsonElement unknownKind = Parse("""{ "Name": "x", "Products": [ { "kind": "game", "Sku": "G-1", "Price": 1 } ] }""");

        foreach (JsonSchemaDialect dialect in (JsonSchemaDialect[])[JsonSchemaDialect.Draft07, JsonSchemaDialect.Draft201909, JsonSchemaDialect.Draft202012])
        {
            JsonSchemaDocument document = JsonSchemaDocument.Generate(
                SampleContracts.Default.Catalog,
                new JsonSchemaGenerationOptions { Dialect = dialect },
                new JsonSchemaDocumentOptions { ValidateSchema = true });
            string definitions = dialect == JsonSchemaDialect.Draft07 ? "definitions" : "$defs";

            Check("generate " + dialect.Id + ": a shared recursive type is defined", document.Source.GetProperty(definitions).TryGetProperty("Category", out _));
            Check("generate " + dialect.Id + ": the serialized value round-trips", document.IsValid(instance));
            Check("generate " + dialect.Id + ": an unknown discriminator is rejected", !document.IsValid(unknownKind));
        }
    }

    private static void Check(string name, bool passed)
    {
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
        if (!passed)
        {
            _failures++;
        }
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
