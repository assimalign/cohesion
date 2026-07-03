using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the credential model: state and temporal usability plus
/// invalid-state guards.
/// </summary>
public sealed class IdentityCredentialTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Credential: IsUsable should combine state and validity window")]
    public void IsUsable_WhenEvaluated_ShouldCombineStateAndValidityWindow()
    {
        // Arrange — an active certificate valid from yesterday to tomorrow.
        var credential = new IdentityCredential(new IdentityCredentialDescriptor
        {
            Id = "thumbprint-1",
            Kind = IdentityCredentialKind.Certificate,
            State = IdentityCredentialState.Active,
            NotBefore = now.AddDays(-1),
            ExpiresAt = now.AddDays(1),
        });

        // Assert
        credential.IsUsable(now).ShouldBeTrue();
        credential.IsUsable(now.AddDays(-2)).ShouldBeFalse();       // before NotBefore
        credential.IsUsable(now.AddDays(2)).ShouldBeFalse();        // after ExpiresAt
        credential.IsUsable(credential.ExpiresAt!.Value).ShouldBeFalse(); // expiry is exclusive
        credential.IsUsable(credential.NotBefore!.Value).ShouldBeTrue(); // start is inclusive
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Credential: Non-active states should never be usable")]
    public void IsUsable_WhenStateIsNotActive_ShouldBeFalse()
    {
        foreach (var state in new[]
        {
            IdentityCredentialState.Unknown,
            IdentityCredentialState.Suspended,
            IdentityCredentialState.Revoked,
        })
        {
            var credential = new IdentityCredential(new IdentityCredentialDescriptor
            {
                Id = "credential-1",
                State = state,
            });

            credential.IsUsable(now).ShouldBeFalse($"state {state} must not be usable");
        }

        // A forgotten state assignment defaults to Unknown — and Unknown is never usable.
        var defaulted = new IdentityCredential(new IdentityCredentialDescriptor { Id = "defaulted" });
        defaulted.State.ShouldBe(IdentityCredentialState.Unknown);
        defaulted.IsUsable(now).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Credential: Materialization should reject invalid descriptors")]
    public void Constructor_WhenDescriptorIsInvalid_ShouldThrow()
    {
        Should.Throw<IdentityModelException>(() => new IdentityCredential(new IdentityCredentialDescriptor()));
        Should.Throw<IdentityModelException>(() => new IdentityCredential(new IdentityCredentialDescriptor
        {
            Id = "backwards-window",
            NotBefore = now,
            ExpiresAt = now, // must be strictly after NotBefore
        }));
        Should.Throw<ArgumentNullException>(() => new IdentityCredential(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Credential: Model should carry metadata, subject, and never secrets")]
    public void Constructor_WhenConstructed_ShouldCarryMetadata()
    {
        // Arrange
        var descriptor = new IdentityCredentialDescriptor
        {
            Id = "kid-2026-07",
            Kind = IdentityCredentialKind.Key,
            State = IdentityCredentialState.Active,
            Subject = new SubjectIdentifier("client-1", SubjectIdentifierFormats.ClientIdentifier),
            CreatedAt = now.AddDays(-30),
        };
        descriptor.Properties["algorithm"] = "ES256";

        // Act
        var credential = new IdentityCredential(descriptor);
        descriptor.Properties["late"] = "mutation";

        // Assert
        credential.Kind.ShouldBe(IdentityCredentialKind.Key);
        credential.Subject!.Value.ShouldBe("client-1");
        credential.Properties["algorithm"].AsString().ShouldBe("ES256");
        credential.Properties.Count.ShouldBe(1); // snapshot isolated from late mutation
    }
}
