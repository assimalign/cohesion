using FluentAssertions;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiSemanticValidationTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: duplicate operationId is reported")]
    public void Validate_DuplicateOperationId_Reports()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var second = new OpenApiPathItem();
        var operation = new OpenApiOperation { OperationId = "getPet" };
        operation.Responses = new OpenApiResponses();
        operation.Responses.Items["200"] = new OpenApiResponse { Description = "ok" };
        second.Operations[OperationType.Get] = operation;
        document.Paths!.Items["/pets"] = second;

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d => d.Code == OpenApiValidationRuleCodes.DuplicateOperationId);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: undeclared path placeholder is reported")]
    public void Validate_MissingPathParameter_Reports()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var item = new OpenApiPathItem();
        var operation = new OpenApiOperation { OperationId = "getOwner" };
        operation.Responses = new OpenApiResponses();
        operation.Responses.Items["200"] = new OpenApiResponse { Description = "ok" };
        item.Operations[OperationType.Get] = operation;
        document.Paths!.Items["/owners/{ownerId}"] = item;

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d => d.Code == OpenApiValidationRuleCodes.MissingPathParameter);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: path parameter must be required")]
    public void Validate_PathParameterNotRequired_Reports()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Parameters[0].Required = false;

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d => d.Code == OpenApiValidationRuleCodes.PathParameterNotRequired);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: path parameter not in template is reported")]
    public void Validate_UndeclaredPathParameter_Reports()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Parameters.Add(new OpenApiParameter
        {
            Name = "ghost",
            In = ParameterLocation.Path,
            Required = true,
            Schema = new OpenApiSchema { Type = SchemaType.String }
        });

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d => d.Code == OpenApiValidationRuleCodes.UndeclaredPathParameter);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: undefined security scheme is reported")]
    public void Validate_UnknownSecurityScheme_Reports()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes["does_not_exist"] = new System.Collections.Generic.List<string>();
        document.Security.Add(requirement);

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d =>
            d.Code == OpenApiValidationRuleCodes.UnknownSecurityScheme);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: invalid response key is reported")]
    public void Validate_InvalidResponseKey_Reports()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["banana"] =
            new OpenApiResponse { Description = "nope" };

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d => d.Code == OpenApiValidationRuleCodes.InvalidResponseKey);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: parameter without schema or content is reported")]
    public void Validate_ParameterWithoutSchemaOrContent_Reports()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Parameters[0].Schema = null;

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d => d.Code == OpenApiValidationRuleCodes.ParameterSchemaAndContent);
    }
}
