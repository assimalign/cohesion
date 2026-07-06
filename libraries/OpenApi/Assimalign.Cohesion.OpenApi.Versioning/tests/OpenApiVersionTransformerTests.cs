using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Versioning.Tests;

public class OpenApiVersionTransformerTests
{
    private static OpenApiDocument Minimal(OpenApiSpecVersion version) => new()
    {
        SpecVersion = version,
        Info = new OpenApiInfo { Title = "t", Version = "1.0.0" },
        Components = new OpenApiComponents()
    };

    // ------------------------------------------------------------------ upgrades (3.0 -> 3.1)

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: nullable becomes a type array on serialization")]
    public void Upgrade_Nullable_BecomesTypeArray()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"] = new OpenApiSchema { Type = SchemaType.String, Nullable = true };

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        result.Document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
        result.Document.ToJson(indented: false).ShouldContain("\"type\":[\"string\",\"null\"]", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: exclusive bounds become numeric on serialization")]
    public void Upgrade_ExclusiveBounds_BecomeNumeric()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"] = new OpenApiSchema { Type = SchemaType.Integer, ExclusiveMaximum = 10 };

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        result.Document.ToJson(indented: false).ShouldContain("\"exclusiveMaximum\":10", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: binary format converts to a content keyword")]
    public void Upgrade_BinaryFormat_Converted()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["File"] = new OpenApiSchema { Type = SchemaType.String, Format = "binary" };

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        var schema = result.Document.Components!.Schemas["File"];
        schema.Format.ShouldBeNull();
        schema.ContentMediaType.ShouldBe("application/octet-stream");
        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.BinaryFormatConverted);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: schema example moves into the examples array")]
    public void Upgrade_SchemaExample_MovedToExamples()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"] = new OpenApiSchema { Type = SchemaType.String, Example = "sample" };

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        var schema = result.Document.Components!.Schemas["S"];
        schema.Example.ShouldBeNull();
        schema.Examples.Count.ShouldBe(1);
        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.SchemaExampleConverted);
    }

    // ------------------------------------------------------------------ upgrades (3.1 -> 3.2)

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: deprecated XML flags become nodeType")]
    public void Upgrade_XmlFlags_BecomeNodeType()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        document.Components!.Schemas["S"] = new OpenApiSchema { Xml = new OpenApiXml { Attribute = true } };

        var result = document.TransformTo(OpenApiSpecVersion.V3_2);

        var xml = result.Document.Components!.Schemas["S"].Xml!;
        xml.NodeType.ShouldBe(XmlNodeType.Attribute);
        xml.Attribute.ShouldBeFalse();
        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: 3.0 to 3.2 produces a valid target document")]
    public void Upgrade_ThreeZeroToThreeTwo_IsValid()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"] = new OpenApiSchema { Type = SchemaType.String, Nullable = true, Format = "byte" };

        var result = document.TransformTo(OpenApiSpecVersion.V3_2);

        result.Document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_2);
        result.Document.Validate().IsValid.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ downgrades

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: const becomes a single-value enum")]
    public void Downgrade_Const_BecomesEnum()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        document.Components!.Schemas["S"] = new OpenApiSchema { Type = SchemaType.String, Const = "fixed" };

        var result = document.TransformTo(OpenApiSpecVersion.V3_0);

        var schema = result.Document.Components!.Schemas["S"];
        schema.Const.ShouldBeNull();
        schema.Enum.Count.ShouldBe(1);
        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.ConstConverted);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: a multi-type schema is reduced with a warning")]
    public void Downgrade_MultiType_Reduced()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        var schema = new OpenApiSchema();
        schema.Types.Add(SchemaType.String);
        schema.Types.Add(SchemaType.Integer);
        document.Components!.Schemas["S"] = schema;

        var result = document.TransformTo(OpenApiSpecVersion.V3_0);

        result.Document.Components!.Schemas["S"].Types.Count.ShouldBe(1);
        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.MultiTypeReduced && d.Severity == OpenApiTransformSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: webhooks are reported as unsupported")]
    public void Downgrade_Webhooks_ReportedUnsupported()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        document.Webhooks["onEvent"] = new OpenApiPathItem();

        var result = document.TransformTo(OpenApiSpecVersion.V3_0);

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.UnsupportedConstruct && d.Location == "#/webhooks");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: 3.2 $self is reported as unsupported in 3.1")]
    public void Downgrade_Self_ReportedUnsupported()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Self = "https://example.com/openapi.json";

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.UnsupportedConstruct && d.Location == "#/$self");
    }

    // ------------------------------------------------------------------ invariants

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Transform: the input document is not mutated")]
    public void Transform_DoesNotMutateInput()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"] = new OpenApiSchema { Type = SchemaType.String, Format = "binary" };

        document.TransformTo(OpenApiSpecVersion.V3_1);

        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"].Format.ShouldBe("binary");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Transform: same version is an identity with no diagnostics")]
    public void Transform_SameVersion_Identity()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        result.Diagnostics.ShouldBeEmpty();
        result.Document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Transform: the result serializes at the target line")]
    public void Transform_Result_SerializesAtTarget()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);

        var result = document.TransformTo(OpenApiSpecVersion.V3_2);

        result.Document.ToJson(indented: false).ShouldContain("\"openapi\":\"3.2.0\"", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: nested schema conversions apply recursively")]
    public void Downgrade_NestedSchema_Converted()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        var root = new OpenApiSchema { Type = SchemaType.Object };
        root.Properties["code"] = new OpenApiSchema { Type = SchemaType.String, Const = "A" };
        document.Components!.Schemas["Root"] = root;

        var result = document.TransformTo(OpenApiSpecVersion.V3_0);

        result.Document.Components!.Schemas["Root"].Properties["code"].Const.ShouldBeNull();
        result.Document.Components.Schemas["Root"].Properties["code"].Enum.Count.ShouldBe(1);
    }

    // ------------------------------------------------------------------ regression coverage

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: a list-valued schema examples array survives the copy")]
    public void Upgrade_ListValuedExamples_Survives()
    {
        // The deep copy serializes at the superset line, so a 3.1-only construct carried on a
        // 3.0-declared document is not gated away before the transform runs.
        var document = Minimal(OpenApiSpecVersion.V3_0);
        var schema = new OpenApiSchema { Type = SchemaType.String };
        schema.Examples.Add("a");
        schema.Examples.Add("b");
        document.Components!.Schemas["S"] = schema;

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        result.Document.Components!.Schemas["S"].Examples.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: const in a response header schema is converted")]
    public void Downgrade_ResponseHeaderSchema_Converted()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        var operation = new OpenApiOperation();
        operation.Responses = new OpenApiResponses();
        var response = new OpenApiResponse { Description = "ok" };
        response.Headers["X-Kind"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = SchemaType.String, Const = "A" } };
        operation.Responses.Items["200"] = response;
        var pathItem = new OpenApiPathItem();
        pathItem.Operations[OperationType.Get] = operation;
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/f"] = pathItem;

        var result = document.TransformTo(OpenApiSpecVersion.V3_0);

        var headerSchema = result.Document.Paths!.Items["/f"].Operations[OperationType.Get].Responses!.Items["200"].Headers["X-Kind"].Schema!;
        headerSchema.Const.ShouldBeNull();
        headerSchema.Enum.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: binary format in a component response schema is converted")]
    public void Upgrade_ComponentResponseSchema_Converted()
    {
        var document = Minimal(OpenApiSpecVersion.V3_0);
        var response = new OpenApiResponse { Description = "file" };
        response.Content["application/octet-stream"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = SchemaType.String, Format = "binary" } };
        document.Components!.Responses["File"] = response;

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        var schema = result.Document.Components!.Responses["File"].Content["application/octet-stream"].Schema!;
        schema.Format.ShouldBeNull();
        schema.ContentMediaType.ShouldBe("application/octet-stream");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: const in a path-item-level parameter is converted")]
    public void Downgrade_PathItemParameterSchema_Converted()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        var pathItem = new OpenApiPathItem();
        pathItem.Parameters.Add(new OpenApiParameter { Name = "v", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = SchemaType.String, Const = "A" } });
        pathItem.Operations[OperationType.Get] = new OpenApiOperation();
        document.Paths = new OpenApiPaths();
        document.Paths.Items["/f"] = pathItem;

        var result = document.TransformTo(OpenApiSpecVersion.V3_0);

        result.Document.Paths!.Items["/f"].Parameters[0].Schema!.Const.ShouldBeNull();
        result.Document.Paths.Items["/f"].Parameters[0].Schema!.Enum.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Downgrade: a non-representable XML nodeType is dropped with a warning")]
    public void Downgrade_XmlTextNodeType_WarnsAndDrops()
    {
        var document = Minimal(OpenApiSpecVersion.V3_2);
        document.Components!.Schemas["S"] = new OpenApiSchema { Xml = new OpenApiXml { NodeType = XmlNodeType.Text } };

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        var xml = result.Document.Components!.Schemas["S"].Xml!;
        xml.NodeType.ShouldBeNull();
        xml.Attribute.ShouldBeFalse();
        xml.Wrapped.ShouldBeFalse();
        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.UnsupportedConstruct && d.Severity == OpenApiTransformSeverity.Warning);
        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Versioning] - Upgrade: both XML flags set keeps attribute and warns on dropped wrapped")]
    public void Upgrade_BothXmlFlags_WarnsOnDroppedWrapped()
    {
        var document = Minimal(OpenApiSpecVersion.V3_1);
        document.Components!.Schemas["S"] = new OpenApiSchema { Xml = new OpenApiXml { Attribute = true, Wrapped = true } };

        var result = document.TransformTo(OpenApiSpecVersion.V3_2);

        var xml = result.Document.Components!.Schemas["S"].Xml!;
        xml.NodeType.ShouldBe(XmlNodeType.Attribute);
        xml.Wrapped.ShouldBeFalse();
        result.Diagnostics.ShouldContain(d => d.Code == OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted && d.Severity == OpenApiTransformSeverity.Warning);
    }
}
