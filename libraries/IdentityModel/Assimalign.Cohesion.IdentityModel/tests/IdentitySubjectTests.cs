using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the canonical identity subject: kind differentiation, actor
/// delegation chains, identifier semantics, and invalid-state guards.
/// </summary>
public sealed class IdentitySubjectTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Subject: Kinds should differentiate and default honestly")]
    public void Kind_WhenMaterialized_ShouldDifferentiateAndDefaultHonestly()
    {
        // An uninitialized descriptor kind is Unknown — never a fabricated User.
        var unknown = new IdentitySubject(new IdentitySubjectDescriptor
        {
            Identifier = new SubjectIdentifier("actor-1"),
        });
        var user = new IdentitySubject(new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier("user-1"),
        });
        var application = new IdentitySubject(new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.Application,
            Identifier = new SubjectIdentifier("client-1", SubjectIdentifierFormats.ClientIdentifier),
        });

        unknown.Kind.ShouldBe(IdentityKind.Unknown);
        user.Kind.ShouldBe(IdentityKind.User);
        application.Kind.ShouldBe(IdentityKind.Application);
        ((int)IdentityKind.Unknown).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Subject: Materialization should require a primary identifier")]
    public void Constructor_WhenIdentifierIsMissing_ShouldThrowIdentityModelException()
    {
        Should.Throw<IdentityModelException>(() => new IdentitySubject(new IdentitySubjectDescriptor()));
        Should.Throw<ArgumentNullException>(() => new IdentitySubject(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Subject: Identifiers should lead with the primary and deduplicate")]
    public void Identifiers_WhenMaterialized_ShouldLeadWithPrimaryAndDeduplicate()
    {
        // Arrange
        var primary = new SubjectIdentifier("user-1", issuer: "https://idp-a.example");
        var federated = new SubjectIdentifier("u-9931", issuer: "https://idp-b.example");
        var descriptor = new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = primary,
        };
        descriptor.AdditionalIdentifiers.Add(federated);
        descriptor.AdditionalIdentifiers.Add(new SubjectIdentifier("user-1", issuer: "https://idp-a.example")); // dup of primary
        descriptor.AdditionalIdentifiers.Add(federated); // dup of itself

        // Act
        var subject = new IdentitySubject(descriptor);

        // Assert
        subject.Identifiers.Count.ShouldBe(2);
        subject.Identifiers[0].ShouldBe(primary);
        subject.Identifiers[1].ShouldBe(federated);
        subject.Identifier.ShouldBe(primary);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Subject: Actor chains should represent delegation")]
    public void Actor_WhenChained_ShouldRepresentDelegation()
    {
        // Arrange — an RFC 8693 style chain: a client acting for a user. The actor entry
        // only carries a client_id, so its kind is honestly Unknown.
        var actor = new IdentitySubject(new IdentitySubjectDescriptor
        {
            Identifier = new SubjectIdentifier("service-client", SubjectIdentifierFormats.ClientIdentifier),
        });
        var descriptor = new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier("user-1"),
            Actor = actor,
        };
        descriptor.Claims.Add(new IdentityClaim(IdentityClaimTypes.Email, "user@example.com"));

        // Act
        var subject = new IdentitySubject(descriptor);

        // Assert
        subject.Actor.ShouldBeSameAs(actor);
        subject.Actor!.Actor.ShouldBeNull();
        subject.Claims.GetString(IdentityClaimTypes.Email).ShouldBe("user@example.com");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Subject: Actor chains beyond the maximum depth should be rejected")]
    public void Constructor_WhenActorChainExceedsMaxDepth_ShouldThrowIdentityModelException()
    {
        // Arrange — a chain of exactly MaxActorDepth is legal as an Actor value, so build
        // one level past it: the loop's last construction still sees a legal chain, and the
        // final descriptor below is the first to exceed the cap.
        IIdentitySubject? actor = null;
        for (var depth = 0; depth <= IdentitySubject.MaxActorDepth; depth++)
        {
            actor = new IdentitySubject(new IdentitySubjectDescriptor
            {
                Identifier = new SubjectIdentifier($"actor-{depth}"),
                Actor = actor,
            });
        }

        var descriptor = new IdentitySubjectDescriptor
        {
            Identifier = new SubjectIdentifier("one-too-deep"),
            Actor = actor,
        };

        // Act + Assert
        Should.Throw<IdentityModelException>(() => new IdentitySubject(descriptor));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Subject: Cyclic foreign actor implementations should be rejected")]
    public void Constructor_WhenActorChainIsCyclic_ShouldThrowIdentityModelException()
    {
        // Arrange — a hostile or buggy third-party IIdentitySubject that cycles.
        var cyclic = new CyclicSubject();

        var descriptor = new IdentitySubjectDescriptor
        {
            Identifier = new SubjectIdentifier("victim"),
            Actor = cyclic,
        };

        // Act + Assert — the depth cap terminates the walk instead of hanging.
        Should.Throw<IdentityModelException>(() => new IdentitySubject(descriptor));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Subject: Descriptor mutation after materialization should not leak")]
    public void Constructor_WhenDescriptorMutatesAfterwards_ShouldNotChange()
    {
        // Arrange
        var descriptor = new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier("user-1"),
        };
        descriptor.Claims.Add(new IdentityClaim("email", "a@example.com"));

        var subject = new IdentitySubject(descriptor);

        // Act
        descriptor.Claims.Add(new IdentityClaim("email", "b@example.com"));
        descriptor.AdditionalIdentifiers.Add(new SubjectIdentifier("late"));

        // Assert
        subject.Claims.Count.ShouldBe(1);
        subject.Identifiers.Count.ShouldBe(1);
    }

    private sealed class CyclicSubject : IIdentitySubject
    {
        public IdentityKind Kind => IdentityKind.Unknown;
        public SubjectIdentifier Identifier { get; } = new("cycle");
        public IReadOnlyList<SubjectIdentifier> Identifiers => [Identifier];
        public string? DisplayName => null;
        public IIdentityClaimCollection Claims => IdentityClaimCollection.Empty;
        public IIdentitySubject? Actor => this;
    }
}
