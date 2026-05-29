using FluentAssertions;
using Xunit;

using Assimalign.Cohesion.OpenApi.Serialization;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiEndToEndTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi] - EndToEnd: author, emit 3.1.2 JSON, reparse, validate clean")]
    public void Author_Emit_Reparse_Validate_IsClean()
    {
        // Arrange: author a document in the object model.
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);

        // Act: emit JSON for 3.1.2, then read it back.
        var json = document.ToJson(OpenApiSpecVersion.V3_1);
        var reparsed = OpenApiJson.Parse(json);
        var result = reparsed.Validate();

        // Assert: the emitted document targets 3.1.2, round-trips, and validates clean.
        json.Should().Contain("\"openapi\": \"3.1.2\"");
        reparsed.SpecVersion.Should().Be(OpenApiSpecVersion.V3_1);
        reparsed.Paths!.Items.Should().ContainKey("/pets/{id}");
        result.IsValid.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - EndToEnd: emitting 3.0 from a 3.1 model adapts version-gated fields")]
    public void Emit_ThreeZero_FromModel_AdaptsVersionGatedFields()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Components!.Schemas["Pet"] = new OpenApiSchema { Type = SchemaType.String, Nullable = true };

        var json = document.ToJson(OpenApiSpecVersion.V3_0, indented: false);

        json.Should().Contain("\"openapi\":\"3.0.4\"");
        json.Should().Contain("\"nullable\":true");
    }
}
