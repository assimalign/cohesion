using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiAdvancedSurfaceValidationTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Links: operationId and operationRef together are a conflict")]
    public void Validate_LinkWithBothOperationForms_ReportsConflict()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var response = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"];
        response.Links["self"] = new OpenApiLink { OperationId = "getPet", OperationRef = "#/paths/x/get" };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.LinkOperationConflict && d.Location.EndsWith("/links/self"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Links: a single operation form is valid")]
    public void Validate_LinkWithOneOperationForm_NoConflict()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        var response = document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"];
        response.Links["self"] = new OpenApiLink { OperationId = "getPet" };

        var result = document.Validate();

        result.Diagnostics.ShouldNotContain(d => d.Code == OpenApiValidationRuleCodes.LinkOperationConflict);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Links: a component link conflict is located under components")]
    public void Validate_ComponentLinkConflict_Located()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Components!.Links["sub"] = new OpenApiLink { OperationId = "getPet", OperationRef = "#/paths/x/get" };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.LinkOperationConflict && d.Location == "#/components/links/sub");
    }
}
