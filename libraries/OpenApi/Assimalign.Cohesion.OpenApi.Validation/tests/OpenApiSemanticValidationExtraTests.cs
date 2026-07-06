using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiSemanticValidationExtraTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: duplicate name+location parameters are rejected")]
    public void Validate_DuplicateParameter_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.Parameters.Add(new OpenApiParameter { Name = "id", In = ParameterLocation.Path, Required = true, Schema = new OpenApiSchema { Type = SchemaType.Integer } });

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.DuplicateParameter);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: querystring parameter must use content")]
    public void Validate_QuerystringWithSchema_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        operation.Parameters.Add(new OpenApiParameter { Name = "q", In = ParameterLocation.Querystring, Schema = new OpenApiSchema { Type = SchemaType.String } });

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.QuerystringParameterUsage);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: querystring must not coexist with query parameters")]
    public void Validate_QuerystringWithQuery_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        var qs = new OpenApiParameter { Name = "filter", In = ParameterLocation.Querystring };
        qs.Content["application/x-www-form-urlencoded"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = SchemaType.Object } };
        operation.Parameters.Add(qs);
        operation.Parameters.Add(new OpenApiParameter { Name = "page", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = SchemaType.Integer } });

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.QuerystringParameterUsage && d.Message.Contains("coexist"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: a well-formed querystring parameter is valid")]
    public void Validate_QuerystringWithContentOnly_NoError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        var operation = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get];
        var qs = new OpenApiParameter { Name = "filter", In = ParameterLocation.Querystring };
        qs.Content["application/x-www-form-urlencoded"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = SchemaType.Object } };
        operation.Parameters.Add(qs);

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.QuerystringParameterUsage);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: example value and externalValue conflict")]
    public void Validate_ExampleValueConflict_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Components!.Examples["pet"] = new OpenApiExample
        {
            Value = OpenApiValueNode.String("inline"),
            ExternalValue = "https://example.com/pet.json"
        };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.ExampleValueConflict);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: example dataValue and serializedValue coexist")]
    public void Validate_ExampleDataAndSerialized_NoConflict()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Components!.Examples["pet"] = new OpenApiExample
        {
            DataValue = new OpenApiObjectNode { ["name"] = "Rex" },
            SerializedValue = "name=Rex"
        };

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.ExampleValueConflict);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: incomplete authorizationCode flow is reported")]
    public void Validate_IncompleteOAuthFlow_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Components!.SecuritySchemes["oauth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow { AuthorizationUrl = "https://example.com/authorize" }
            }
        };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.IncompleteOAuthFlow && d.Location.EndsWith("/tokenUrl"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: device flow requires deviceAuthorizationUrl")]
    public void Validate_DeviceFlowMissingUrl_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Components!.SecuritySchemes["oauth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                DeviceAuthorization = new OpenApiOAuthFlow { TokenUrl = "https://example.com/token" }
            }
        };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.IncompleteOAuthFlow && d.Location.EndsWith("/deviceAuthorizationUrl"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: additionalOperations must not use a fixed method")]
    public void Validate_ReservedAdditionalOperation_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Paths!.Items["/pets/{id}"].AdditionalOperations["POST"] = new OpenApiOperation { OperationId = "reservedPost" };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.ReservedAdditionalOperation);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: a non-fixed additionalOperations method is allowed")]
    public void Validate_CustomAdditionalOperation_NoError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        var operation = new OpenApiOperation { OperationId = "purge" };
        operation.Responses = new OpenApiResponses();
        operation.Responses.Items["200"] = new OpenApiResponse { Description = "Purged." };
        document.Paths!.Items["/pets/{id}"].AdditionalOperations["PURGE"] = operation;

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.ReservedAdditionalOperation);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: 3.2 security requirement may name a scheme URI")]
    public void Validate_SecurityRequirementUri_NoError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes["https://auth.example.com/schemes/oauth"] = [];
        document.Security.Add(requirement);

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.UnknownSecurityScheme);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: pre-3.2 security requirement URI is still unknown")]
    public void Validate_SecurityRequirementUriInThreeOne_ReportsUnknown()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes["https://auth.example.com/schemes/oauth"] = [];
        document.Security.Add(requirement);

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.UnknownSecurityScheme);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: 3.2 allows an omitted response description")]
    public void Validate_ResponseWithoutDescriptionInThreeTwo_NoError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"] = new OpenApiResponse { Summary = "OK" };

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.RequiredField && d.Location.EndsWith("/description"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: pre-3.2 still requires a response description")]
    public void Validate_ResponseWithoutDescriptionInThreeOne_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"] = new OpenApiResponse();

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.RequiredField && d.Location.EndsWith("/description"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: a path parameter declared via $ref is recognized")]
    public void Validate_PathParameterViaReference_NotReportedMissing()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);

        // Move the path parameter into components and reference it from the path item.
        document.Components!.Parameters["idParam"] = new OpenApiParameter
        {
            Name = "id",
            In = ParameterLocation.Path,
            Required = true,
            Schema = new OpenApiSchema { Type = SchemaType.Integer }
        };
        var pathItem = document.Paths!.Items["/pets/{id}"];
        pathItem.Operations[OperationType.Get].Parameters.Clear();
        pathItem.Parameters.Add(new OpenApiParameter { Reference = new OpenApiReference { Ref = "#/components/parameters/idParam" } });

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.MissingPathParameter);
        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.UndeclaredPathParameter);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: an unresolvable parameter ref suppresses the missing check")]
    public void Validate_ExternalParameterReference_SuppressesMissingCheck()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var pathItem = document.Paths!.Items["/pets/{id}"];
        pathItem.Operations[OperationType.Get].Parameters.Clear();
        pathItem.Parameters.Add(new OpenApiParameter { Reference = new OpenApiReference { Ref = "common.yaml#/components/parameters/idParam" } });

        var result = document.Validate();

        // The external reference might declare {id}; we cannot resolve it, so we must not report it missing.
        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.MissingPathParameter);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Semantic: a genuinely missing path parameter is still reported")]
    public void Validate_GenuinelyMissingPathParameter_ReportsError()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Parameters.Clear();
        document.Paths.Items["/pets/{id}"].Parameters.Clear();

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.MissingPathParameter);
    }
}
