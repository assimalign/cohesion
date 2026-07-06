using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Validation;
using Assimalign.Cohesion.OpenApi.Versioning;

namespace Assimalign.Cohesion.OpenApi.Integration.Tests;

public class OpenApiApiManagerIntegrationTests
{
    private const string SampleJson = """
        {"openapi":"3.1.2","info":{"title":"Imported","version":"1.0.0"},"webhooks":{"onEvent":{"post":{"responses":{"200":{"description":"ok"}}}}},"paths":{"/ping":{"get":{"operationId":"ping","responses":{"200":{"description":"pong"}}}}}}
        """;

    [Fact(DisplayName = "Cohesion Test [OpenApi.Integration] - ApiManager: import parses JSON and YAML into the same model")]
    public void ApiManager_Import_JsonAndYaml()
    {
        var importer = OpenApiIntegration.CreateImporter();
        var exporter = OpenApiIntegration.CreateExporter();

        var fromJson = importer.Import(SampleJson, OpenApiFormat.Json);
        var yaml = exporter.Export(fromJson, OpenApiFormat.Yaml);
        var fromYaml = importer.Import(yaml, OpenApiFormat.Yaml);

        fromJson.Info.Title.ShouldBe("Imported");
        fromYaml.Paths!.Items.ShouldContainKey("/ping");
        fromYaml.Validate().IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Integration] - ApiManager: export retargets and reports lossy diagnostics")]
    public void ApiManager_Export_RetargetsWithDiagnostics()
    {
        var importer = OpenApiIntegration.CreateImporter();
        var exporter = OpenApiIntegration.CreateExporter();

        var document = importer.Import(SampleJson, OpenApiFormat.Json);
        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);

        var result = exporter.Export(document, OpenApiFormat.Json, OpenApiSpecVersion.V3_0);

        result.Content.ShouldContain("\"openapi\": \"3.0.4\"", Case.Sensitive);
        result.Content.ShouldNotContain("webhooks", Case.Sensitive);
        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiTransformDiagnosticCodes.UnsupportedConstruct && d.Location.StartsWith("#/webhooks"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Integration] - ApiManager: export at the same line raises no diagnostics")]
    public void ApiManager_Export_SameVersion_NoDiagnostics()
    {
        var importer = OpenApiIntegration.CreateImporter();
        var exporter = OpenApiIntegration.CreateExporter();

        var document = importer.Import(SampleJson, OpenApiFormat.Json);
        var result = exporter.Export(document, OpenApiFormat.Yaml, OpenApiSpecVersion.V3_1);

        result.Diagnostics.ShouldBeEmpty();
        result.Content.ShouldContain("openapi:", Case.Sensitive);
    }
}
