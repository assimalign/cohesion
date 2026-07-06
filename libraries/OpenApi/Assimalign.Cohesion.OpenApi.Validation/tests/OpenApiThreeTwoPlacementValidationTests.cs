using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiThreeTwoPlacementValidationTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: server name in 3.1 is unsupported")]
    public void Validate_ServerNameInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Servers.Add(new OpenApiServer { Url = "https://api.example.com", Name = "production" });

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/servers/0/name");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: query operation in 3.1 is unsupported")]
    public void Validate_QueryOperationInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Query] = new OpenApiOperation { OperationId = "queryPets" };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location.EndsWith("/query"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: additionalOperations in 3.1 is unsupported")]
    public void Validate_AdditionalOperationsInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].AdditionalOperations["COPY"] = new OpenApiOperation { OperationId = "copyPet" };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location.EndsWith("/additionalOperations"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: querystring parameter in 3.1 is unsupported")]
    public void Validate_QuerystringParameterInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.Parameters.Add(new OpenApiParameter { Name = "filter", In = ParameterLocation.Querystring });

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location.EndsWith("/parameters/1/in"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: response summary in 3.1 is unsupported")]
    public void Validate_ResponseSummaryInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.Responses!.Items["200"].Summary = "Result.";

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location.EndsWith("/responses/200/summary"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: media streaming fields in 3.1 are unsupported")]
    public void Validate_MediaStreamingFieldsInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        var media = new OpenApiMediaType { ItemSchema = new OpenApiSchema { Type = SchemaType.Object } };
        operation.Responses!.Items["200"].Content["application/jsonl"] = media;

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location.EndsWith("/itemSchema"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: example dataValue in 3.1 is unsupported")]
    public void Validate_ExampleDataValueInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.Parameters[0].Examples["sample"] = new OpenApiExample { DataValue = OpenApiValueNode.Integer(42) };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location.EndsWith("/examples/sample"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: oauth2MetadataUrl and deprecated in 3.1 are unsupported")]
    public void Validate_SecuritySchemeThreeTwoFieldsInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Components!.SecuritySchemes["oauth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            OAuth2MetadataUrl = "https://auth.example.com/.well-known/oauth-authorization-server",
            Deprecated = true
        };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/securitySchemes/oauth/oauth2MetadataUrl");
        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/securitySchemes/oauth/deprecated");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: components mediaTypes in 3.1 is unsupported")]
    public void Validate_ComponentsMediaTypesInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Components!.MediaTypes["PetJson"] = new OpenApiMediaType();

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/mediaTypes");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: multi-type schema in 3.0 is unsupported")]
    public void Validate_MultiTypeSchemaInThreeZero_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        var schema = new OpenApiSchema();
        schema.Types.Add(SchemaType.String);
        schema.Types.Add(SchemaType.Integer);
        document.Components!.Schemas["S"] = schema;

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/schemas/S/type");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: boolean schema form in 3.0 is unsupported")]
    public void Validate_BooleanSchemaInThreeZero_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"] = new OpenApiSchema { BooleanValue = true };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/schemas/S");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: $ref siblings in 3.0 are unsupported")]
    public void Validate_ReferenceSiblingsInThreeZero_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        document.Components!.Schemas["S"] = new OpenApiSchema
        {
            Reference = new OpenApiReference { Ref = "#/components/schemas/Base" },
            Description = "Narrowed."
        };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/schemas/S");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: 2020-12 keywords in 3.0 are unsupported")]
    public void Validate_ExtendedVocabularyInThreeZero_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        var schema = new OpenApiSchema { Type = SchemaType.Object };
        schema.PatternProperties["^x-"] = new OpenApiSchema { Type = SchemaType.String };
        document.Components!.Schemas["S"] = schema;

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/schemas/S");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: xml nodeType and discriminator defaultMapping in 3.1 are unsupported")]
    public void Validate_XmlNodeTypeAndDefaultMappingInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Components!.Schemas["S"] = new OpenApiSchema
        {
            Xml = new OpenApiXml { NodeType = XmlNodeType.Attribute },
            Discriminator = new OpenApiDiscriminator { PropertyName = "kind", DefaultMapping = "Dog" }
        };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/schemas/S/xml/nodeType");
        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/schemas/S/discriminator/defaultMapping");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: 3.2 surfaces produce no diagnostics in 3.2")]
    public void Validate_ThreeTwoSurfacesInThreeTwo_NoVersionDiagnostics()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Servers.Add(new OpenApiServer { Url = "https://api.example.com", Name = "production" });
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"].Summary = "Result.";
        document.Components!.MediaTypes["PetJson"] = new OpenApiMediaType();
        document.Components.Schemas["S"] = new OpenApiSchema
        {
            Xml = new OpenApiXml { NodeType = XmlNodeType.Element },
            Discriminator = new OpenApiDiscriminator { PropertyName = "kind", DefaultMapping = "Dog" }
        };

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion);
    }
}
