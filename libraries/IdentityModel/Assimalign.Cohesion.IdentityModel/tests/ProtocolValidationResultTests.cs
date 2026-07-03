using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the validation result and diagnostic contracts.
/// </summary>
public sealed class ProtocolValidationResultTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Validation success should be computed from error absence")]
    public void Succeeded_WhenComputed_ShouldReflectErrorAbsence()
    {
        // Warnings and information do not fail a result; a single error does — a
        // contradictory "succeeded with errors" state is unconstructible.
        var clean = ProtocolValidationResult.Success;
        var advisory = new ProtocolValidationResult(
        [
            new ProtocolValidationDiagnostic(ProtocolValidationSeverity.Warning, "deprecated_alg", "The algorithm is deprecated."),
            new ProtocolValidationDiagnostic(ProtocolValidationSeverity.Information, "cached_metadata", "Metadata served from cache."),
        ]);
        var failed = new ProtocolValidationResult(
        [
            new ProtocolValidationDiagnostic(ProtocolValidationSeverity.Warning, "deprecated_alg", "The algorithm is deprecated."),
            new ProtocolValidationDiagnostic(ProtocolValidationSeverity.Error, "issuer_mismatch", "The issuer does not match.", member: "iss"),
        ]);

        clean.Succeeded.ShouldBeTrue();
        advisory.Succeeded.ShouldBeTrue();
        advisory.Diagnostics.Count.ShouldBe(2);
        failed.Succeeded.ShouldBeFalse();
        failed.Errors.Count.ShouldBe(1);
        failed.Errors[0].Code.ShouldBe("issuer_mismatch");
        failed.Errors[0].Member.ShouldBe("iss");
        failed.Diagnostics.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Severity ordinals should be stable and fail closed")]
    public void Severity_WhenInspected_ShouldHaveStableFailClosedOrdinals()
    {
        // Error is deliberately the zero value: if a defaulted severity ever leaks it
        // reads as the MOST severe interpretation.
        ((int)ProtocolValidationSeverity.Error).ShouldBe(0);
        ((int)ProtocolValidationSeverity.Warning).ShouldBe(1);
        ((int)ProtocolValidationSeverity.Information).ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Diagnostics should guard their required members")]
    public void Diagnostic_WhenGivenInvalidMembers_ShouldThrow()
    {
        Should.Throw<ArgumentException>(() =>
            new ProtocolValidationDiagnostic(ProtocolValidationSeverity.Error, " ", "message"));
        Should.Throw<ArgumentException>(() =>
            new ProtocolValidationDiagnostic(ProtocolValidationSeverity.Error, "code", ""));
        Should.Throw<ArgumentNullException>(() => new ProtocolValidationResult(null!));
        Should.Throw<ArgumentNullException>(() => new ProtocolValidationResult([null!]));
    }
}
