using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Serialization;

namespace Assimalign.Cohesion.OpenApi.Fluent.Tests;

public class OpenApiFluentVersioningTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: a 3.2-only field on a 3.1 builder throws")]
    public void Build_ThreeTwoFieldOnThreeOne_Throws()
    {
        var builder = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "t", "1");

        Should.Throw<OpenApiException>(() => builder.Self("https://example.com/openapi.json"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: webhooks on a 3.0 builder throws")]
    public void Build_WebhookOnThreeZero_Throws()
    {
        var builder = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_0, "t", "1");

        Should.Throw<OpenApiException>(() => builder.Webhook("onData", _ => { }));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: info.summary on a 3.0 builder throws")]
    public void Build_InfoSummaryOnThreeZero_Throws()
    {
        var builder = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_0, "t", "1");

        Should.Throw<OpenApiException>(() => builder.Info(i => i.Summary("nope")));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: the query operation on a 3.1 builder throws")]
    public void Build_QueryOperationOnThreeOne_Throws()
    {
        var builder = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "t", "1");

        Should.Throw<OpenApiException>(() => builder.Path("/x", path => path.Operation(OperationType.Query, _ => { })));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: 3.2 surfaces build on a 3.2 builder")]
    public void Build_ThreeTwoSurfaces_OnThreeTwo_Succeed()
    {
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_2, "t", "1")
            .Self("https://example.com/openapi.json")
            .Path("/x", path => path
                .Operation(OperationType.Query, op => op.Response("200", r => r.Summary("Result").Description("A result"))))
            .Tag("nav", t => t.Summary("Navigation").Kind("nav"))
            .Build();

        document.Self.ShouldBe("https://example.com/openapi.json");
        document.Paths!.Items["/x"].Operations.ShouldContainKey(OperationType.Query);
        document.Tags[0].Kind.ShouldBe("nav");
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: the builder emits the targeted line's version string")]
    [InlineData(OpenApiSpecVersion.V3_0, "3.0.4")]
    [InlineData(OpenApiSpecVersion.V3_1, "3.1.2")]
    [InlineData(OpenApiSpecVersion.V3_2, "3.2.0")]
    public void Build_TargetedVersion_EmitsVersionString(OpenApiSpecVersion version, string expected)
    {
        var document = OpenApiDocumentBuilder.Create(version, "t", "1")
            .Path("/x", path => path.Operation(OperationType.Get, op => op.Response("200", r => r.Description("ok"))))
            .Build();

        var json = document.ToJson(indented: false);

        json.ShouldContain($"\"openapi\":\"{expected}\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: a nullable schema emits per targeted line")]
    public void Build_NullableSchema_EmitsPerVersion()
    {
        static OpenApiDocument Build(OpenApiSpecVersion version) =>
            OpenApiDocumentBuilder.Create(version, "t", "1")
                .Components(c => c.Schema("Name", s => s.Type(SchemaType.String).Nullable()))
                .Build();

        Build(OpenApiSpecVersion.V3_0).ToJson(indented: false).ShouldContain("\"nullable\":true", Case.Sensitive);
        Build(OpenApiSpecVersion.V3_1).ToJson(indented: false).ShouldContain("\"type\":[\"string\",\"null\"]", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Fluent] - Version: a fluent document round-trips through JSON and YAML")]
    public void Build_Document_RoundTrips()
    {
        var document = OpenApiDocumentBuilder.Create(OpenApiSpecVersion.V3_1, "Petstore", "1.0.0")
            .Path("/pets", path => path.Operation(OperationType.Get, op => op
                .OperationId("listPets")
                .Response("200", r => r.Description("Pets")
                    .Content("application/json", m => m.Schema(s => s.Type(SchemaType.Array).Items(i => i.Type(SchemaType.Object)))))))
            .Build();

        var json = document.ToJson(indented: false);
        OpenApiJson.Parse(json).ToJson(indented: false).ShouldBe(json);

        var yaml = document.ToYaml();
        OpenApiYaml.Parse(yaml).ToYaml().ShouldBe(yaml);
    }
}
