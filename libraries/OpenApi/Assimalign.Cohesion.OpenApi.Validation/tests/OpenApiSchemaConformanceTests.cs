using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiSchemaConformanceTests
{
    private static OpenApiValidationResult ValidateWithSchema(OpenApiDocument document)
    {
        var validator = OpenApiValidation.Create([.. OpenApiValidation.DefaultRules(), OpenApiValidation.CreateOfficialSchemaRule()]);
        return validator.Validate(document);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Validation] - Schema: a valid document produces no schema violations")]
    [InlineData(OpenApiSpecVersion.V3_0)]
    [InlineData(OpenApiSpecVersion.V3_1)]
    [InlineData(OpenApiSpecVersion.V3_2)]
    public void Validate_ValidDocument_NoSchemaViolations(OpenApiSpecVersion version)
    {
        var document = SampleDocuments.CreateValid(version);

        var result = ValidateWithSchema(document);

        var schemaViolations = result.Diagnostics.Where(d => d.Code == OpenApiValidationRuleCodes.SchemaViolation).ToList();
        schemaViolations.ShouldBeEmpty(schemaViolations.Count == 0 ? "" : string.Join("; ", schemaViolations.Select(d => $"{d.Location}: {d.Message}")));
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Validation] - Schema: an empty responses map violates minProperties")]
    [InlineData(OpenApiSpecVersion.V3_0)]
    [InlineData(OpenApiSpecVersion.V3_1)]
    [InlineData(OpenApiSpecVersion.V3_2)]
    public void Validate_EmptyResponsesMap_ReportsSchemaViolation(OpenApiSpecVersion version)
    {
        // The official schema requires at least one response (minProperties: 1); the semantic rules
        // do not check this, so a violation here comes solely from the schema stage.
        var document = SampleDocuments.CreateValid(version);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items.Clear();

        var result = ValidateWithSchema(document);

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.SchemaViolation);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Schema: an invalid enum value is a schema violation")]
    public void Validate_InvalidEnumValue_ReportsSchemaViolation()
    {
        // The security scheme 'type' is constrained to an enum; an unknown value is rejected only by
        // the schema stage.
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var scheme = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, Name = "key", In = ParameterLocation.Header };
        scheme.Extensions["x-note"] = OpenApiValueNode.String("ok");
        document.Components!.SecuritySchemes["broken"] = scheme;
        // A response with a non-status, non-default, non-x- key violates the responses patternProperties.
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["banana"] = new OpenApiResponse { Description = "?" };

        var result = ValidateWithSchema(document);

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.SchemaViolation);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Schema: violations are warnings, not errors")]
    public void Validate_SchemaViolation_IsWarning()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items.Clear();

        var result = ValidateWithSchema(document);

        var schemaDiagnostic = result.Diagnostics.First(d => d.Code == OpenApiValidationRuleCodes.SchemaViolation);
        schemaDiagnostic.Severity.ShouldBe(OpenApiDiagnosticSeverity.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Schema: the default pipeline omits the schema stage")]
    public void DefaultPipeline_OmitsSchemaStage()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items.Clear();

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.SchemaViolation);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Schema: 3.1 nullable type array conforms to the schema")]
    public void Validate_ThreeOneNullableTypeArray_Conforms()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var schema = new OpenApiSchema { Nullable = true };
        schema.Types.Add(SchemaType.String);
        document.Components!.Schemas["Nullable"] = schema;

        var result = ValidateWithSchema(document);

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.SchemaViolation);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Schema: a 3.2 document using 3.2 fields conforms")]
    public void Validate_ThreeTwoDocument_Conforms()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Self = "https://example.com/openapi.json";
        document.Servers.Add(new OpenApiServer { Url = "https://api.example.com", Name = "production" });
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"].Summary = "The pet.";

        var result = ValidateWithSchema(document);

        var schemaViolations = result.Diagnostics.Where(d => d.Code == OpenApiValidationRuleCodes.SchemaViolation).ToList();
        schemaViolations.ShouldBeEmpty(schemaViolations.Count == 0 ? "" : string.Join("; ", schemaViolations.Select(d => $"{d.Location}: {d.Message}")));
    }
}
