using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

/// <summary>
/// Runs the vendored subset of the official YAML test suite (see TestData/yaml-test-suite/README.md
/// for provenance and the documented gaps). Valid cases must parse and match the expected JSON
/// semantics; error cases must raise <see cref="YamlException"/>.
/// </summary>
public class YamlTestSuiteTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var directory in Directory.GetDirectories(SuiteRoot).OrderBy(static d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(directory, "in.yaml")))
            {
                data.Add(Path.GetFileName(directory));
            }
        }

        return data;
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Suite: vendored yaml-test-suite cases")]
    [MemberData(nameof(Cases))]
    public void Suite_VendoredCase_Passes(string id)
    {
        var directory = Path.Combine(SuiteRoot, id);
        var input = File.ReadAllText(Path.Combine(directory, "in.yaml"));
        var expectError = File.Exists(Path.Combine(directory, "error"));

        if (expectError)
        {
            Should.Throw<YamlException>(() => YamlText.Parse(input));
            return;
        }

        var stream = Should.NotThrow(() => YamlText.Parse(input));

        var jsonPath = Path.Combine(directory, "in.json");
        if (!File.Exists(jsonPath))
        {
            return; // Parse success is the whole expectation for cases without a JSON form.
        }

        var expected = ReadJsonDocuments(File.ReadAllText(jsonPath));
        stream.Count.ShouldBe(expected.Count);

        for (var index = 0; index < expected.Count; index++)
        {
            JsonNode.DeepEquals(ToJson(stream[index].Root), expected[index])
                .ShouldBeTrue($"document {index} of case {id} diverges from the expected JSON");
        }
    }

    private static string SuiteRoot => Path.Combine(AppContext.BaseDirectory, "TestData", "yaml-test-suite");

    private static List<JsonNode?> ReadJsonDocuments(string json)
    {
        var documents = new List<JsonNode?>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            documents.Add(JsonNode.Parse(ref reader));
        }

        return documents;
    }

    private static JsonNode? ToJson(YamlNode? node) => node switch
    {
        null => null,
        YamlScalar { Kind: YamlScalarKind.Null } => null,
        YamlScalar { Kind: YamlScalarKind.Boolean } scalar => JsonValue.Create(scalar.GetBoolean()),
        YamlScalar { Kind: YamlScalarKind.Integer } scalar => JsonValue.Create(scalar.GetInteger()),
        YamlScalar { Kind: YamlScalarKind.Float } scalar => JsonValue.Create(scalar.GetDouble()),
        YamlScalar scalar => JsonValue.Create(scalar.Value),
        YamlSequence sequence => new JsonArray([.. sequence.Items.Select(ToJson)]),
        YamlMapping mapping => ToJsonObject(mapping),
        _ => throw new InvalidOperationException($"Unknown node type '{node.GetType().Name}'.")
    };

    private static JsonObject ToJsonObject(YamlMapping mapping)
    {
        var result = new JsonObject();
        foreach (var entry in mapping.Entries)
        {
            var key = entry.Key is YamlScalar scalar ? scalar.Value : "?complex";
            result[key] = ToJson(entry.Value);
        }

        return result;
    }
}
