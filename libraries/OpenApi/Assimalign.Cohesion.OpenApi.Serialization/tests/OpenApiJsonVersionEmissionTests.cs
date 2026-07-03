using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Serialization.Tests;

public class OpenApiJsonVersionEmissionTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit: 3.0 nullable uses the nullable keyword")]
    public void Emit_Nullable_ThreeZero_UsesNullableKeyword()
    {
        var document = WithSchema(OpenApiSpecVersion.V3_0, new OpenApiSchema { Type = SchemaType.String, Nullable = true });

        var json = document.ToJson(OpenApiSpecVersion.V3_0, indented: false);

        json.ShouldContain("\"type\":\"string\"", Case.Sensitive);
        json.ShouldContain("\"nullable\":true", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit: 3.1 nullable uses a type array")]
    public void Emit_Nullable_ThreeOne_UsesTypeArray()
    {
        var document = WithSchema(OpenApiSpecVersion.V3_1, new OpenApiSchema { Type = SchemaType.String, Nullable = true });

        var json = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);

        json.ShouldContain("\"type\":[\"string\",\"null\"]", Case.Sensitive);
        json.ShouldNotContain("nullable", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit: 3.0 exclusive bounds use the boolean form")]
    public void Emit_ExclusiveBounds_ThreeZero_UsesBooleanForm()
    {
        var document = WithSchema(OpenApiSpecVersion.V3_0, new OpenApiSchema { Type = SchemaType.Integer, ExclusiveMaximum = 10 });

        var json = document.ToJson(OpenApiSpecVersion.V3_0, indented: false);

        json.ShouldContain("\"maximum\":10", Case.Sensitive);
        json.ShouldContain("\"exclusiveMaximum\":true", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit: 3.1 exclusive bounds use the numeric form")]
    public void Emit_ExclusiveBounds_ThreeOne_UsesNumericForm()
    {
        var document = WithSchema(OpenApiSpecVersion.V3_1, new OpenApiSchema { Type = SchemaType.Integer, ExclusiveMaximum = 10 });

        var json = document.ToJson(OpenApiSpecVersion.V3_1, indented: false);

        json.ShouldContain("\"exclusiveMaximum\":10", Case.Sensitive);
        json.ShouldNotContain("\"exclusiveMaximum\":true", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit: 3.0 omits info.summary")]
    public void Emit_InfoSummary_ThreeZero_Omitted()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Info.Summary = "Short summary";

        document.ToJson(OpenApiSpecVersion.V3_0, indented: false).ShouldNotContain("\"summary\"", Case.Sensitive);
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldContain("\"summary\":\"Short summary\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit: 3.0 omits webhooks")]
    public void Emit_Webhooks_ThreeZero_Omitted()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Webhooks["onData"] = new OpenApiPathItem();

        document.ToJson(OpenApiSpecVersion.V3_0, indented: false).ShouldNotContain("webhooks", Case.Sensitive);
        document.ToJson(OpenApiSpecVersion.V3_1, indented: false).ShouldContain("\"webhooks\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Serialization] - Emit: openapi field is the canonical version string")]
    public void Emit_OpenApiField_IsCanonicalVersionString()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);

        document.ToJson(OpenApiSpecVersion.V3_2, indented: false).ShouldContain("\"openapi\":\"3.2.0\"", Case.Sensitive);
    }

    private static OpenApiDocument Minimal(OpenApiSpecVersion version) => new()
    {
        SpecVersion = version,
        Info = new OpenApiInfo { Title = "t", Version = "1.0.0" }
    };

    private static OpenApiDocument WithSchema(OpenApiSpecVersion version, OpenApiSchema schema)
    {
        var document = Minimal(version);
        document.Components = new OpenApiComponents();
        document.Components.Schemas["S"] = schema;
        return document;
    }
}
