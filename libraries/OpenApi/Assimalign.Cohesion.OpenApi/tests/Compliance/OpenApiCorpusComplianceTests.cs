using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Compliance.Tests;

/// <summary>
/// Runs the vendored official OpenAPI example corpus through parse, round-trip, and validation, proving
/// the library handles real-world published descriptions across the supported lines and both formats.
/// </summary>
public class OpenApiCorpusComplianceTests
{
    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Corpus: every JSON example parses")]
    [MemberData(nameof(CorpusFixtures.JsonFiles), MemberType = typeof(CorpusFixtures))]
    public void Corpus_Json_Parses(string relativePath)
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative(relativePath));

        document.Info.Title.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Corpus: every JSON example round-trips deterministically")]
    [MemberData(nameof(CorpusFixtures.JsonFiles), MemberType = typeof(CorpusFixtures))]
    public void Corpus_Json_RoundTrips(string relativePath)
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative(relativePath));
        var version = document.SpecVersion;

        var once = document.ToJson(version, indented: false);
        var twice = OpenApiJson.Parse(once).ToJson(version, indented: false);

        twice.ShouldBe(once);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Corpus: JSON and YAML forms produce the same model")]
    [MemberData(nameof(CorpusFixtures.EquivalentJsonYamlPairs), MemberType = typeof(CorpusFixtures))]
    public void Corpus_JsonAndYaml_Equivalent(string jsonPath, string yamlPath)
    {
        var fromJson = OpenApiJson.Parse(CorpusFixtures.ReadRelative(jsonPath));
        var fromYaml = OpenApiYaml.Parse(CorpusFixtures.ReadRelative(yamlPath));

        // Compare via the canonical JSON projection at a common version: equal models emit equal JSON.
        var jsonProjection = fromJson.ToJson(OpenApiSpecVersion.V3_2, indented: false);
        var yamlProjection = fromYaml.ToJson(OpenApiSpecVersion.V3_2, indented: false);

        yamlProjection.ShouldBe(jsonProjection);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Corpus: every YAML example round-trips deterministically")]
    [MemberData(nameof(CorpusFixtures.JsonYamlPairs), MemberType = typeof(CorpusFixtures))]
    public void Corpus_Yaml_RoundTrips(string jsonPath, string yamlPath)
    {
        _ = jsonPath;
        var document = OpenApiYaml.Parse(CorpusFixtures.ReadRelative(yamlPath));

        var once = document.ToYaml();
        var twice = OpenApiYaml.Parse(once).ToYaml();

        twice.ShouldBe(once);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Corpus: every JSON example validates without errors")]
    [MemberData(nameof(CorpusFixtures.JsonFiles), MemberType = typeof(CorpusFixtures))]
    public void Corpus_Json_ValidatesClean(string relativePath)
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative(relativePath));

        var result = document.Validate();

        // Official examples are expected to be valid. Structural/semantic/version errors would indicate
        // either a defect in the example (none are expected) or a false positive in our rules.
        var errors = System.Linq.Enumerable.ToList(result.Errors);
        errors.ShouldBeEmpty(errors.Count == 0
            ? ""
            : $"{relativePath}: {string.Join("; ", errors.Select(e => $"{e.Code} @ {e.Location}: {e.Message}"))}");
    }
}
