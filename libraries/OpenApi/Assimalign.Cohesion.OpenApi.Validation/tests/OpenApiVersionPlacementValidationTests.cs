using FluentAssertions;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Validation.Tests;

public class OpenApiVersionPlacementValidationTests
{
    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: info.summary in 3.0 is unsupported")]
    public void Validate_InfoSummaryInThreeZero_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        document.Info.Summary = "Short summary";

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/info/summary");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: info.summary in 3.1 is supported")]
    public void Validate_InfoSummaryInThreeOne_NoVersionDiagnostic()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Info.Summary = "Short summary";

        var result = document.Validate();

        result.Diagnostics.Should().NotContain(d => d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion);
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: webhooks in 3.0 is unsupported")]
    public void Validate_WebhooksInThreeZero_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        document.Webhooks["onData"] = new OpenApiPathItem();

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/webhooks");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: mutualTLS scheme in 3.0 is unsupported")]
    public void Validate_MutualTlsInThreeZero_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_0);
        document.Components!.SecuritySchemes["mtls"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.MutualTLS };

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/components/securitySchemes/mtls");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: $self in 3.1 is unsupported")]
    public void Validate_SelfInThreeOne_ReportsUnsupported()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_1);
        document.Self = "https://example.com/openapi.json";

        var result = document.Validate();

        result.Diagnostics.Should().Contain(d =>
            d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion && d.Location == "#/$self");
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Validation] - Version: $self in 3.2 is supported")]
    public void Validate_SelfInThreeTwo_NoVersionDiagnostic()
    {
        var document = SampleDocuments.CreateValid(OpenApiSpecVersion.V3_2);
        document.Self = "https://example.com/openapi.json";

        var result = document.Validate();

        result.Diagnostics.Should().NotContain(d => d.Code == OpenApiValidationRuleCodes.UnsupportedInVersion);
    }
}
