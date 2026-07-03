using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiStructuralValidationTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: a valid document produces no diagnostics")]
    public void Validate_ValidDocument_IsClean()
    {
        var result = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1).Validate();

        result.IsValid.ShouldBeTrue();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: missing title is reported")]
    public void Validate_MissingTitle_ReportsRequiredField()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Info.Title = string.Empty;

        var result = document.Validate();

        result.IsValid.ShouldBeFalse();
        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.RequiredField && d.Location == "#/info/title");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: missing version is reported")]
    public void Validate_MissingVersion_ReportsRequiredField()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Info.Version = string.Empty;

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.RequiredField && d.Location == "#/info/version");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: license identifier and url are mutually exclusive")]
    public void Validate_LicenseIdentifierAndUrl_ReportsMutuallyExclusive()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Info.License = new OpenApiLicense { Name = "MIT", Identifier = "MIT", Url = "https://opensource.org/license/mit" };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.MutuallyExclusiveFields);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: path template must start with a slash")]
    public void Validate_PathWithoutLeadingSlash_ReportsInvalidTemplate()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["pets"] = new OpenApiPathItem();

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d => d.Code == OpenApiValidationRuleCodes.InvalidPathTemplate);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: 3.0 requires the paths field")]
    public void Validate_ThreeZeroWithoutPaths_ReportsRequiredField()
    {
        var document = new OpenApiDocument
        {
            SpecVersion = OpenApiSpecVersion.V3_0,
            Info = new OpenApiInfo { Title = "t", Version = "1.0.0" }
        };

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.RequiredField && d.Location == "#/paths");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Structural: missing response description is reported")]
    public void Validate_MissingResponseDescription_ReportsRequiredField()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Paths!.Items["/pets/{id}"].Operations[OperationType.Get].Responses!.Items["200"].Description = string.Empty;

        var result = document.Validate();

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiValidationRuleCodes.RequiredField && d.Location.EndsWith("/description"));
    }
}
