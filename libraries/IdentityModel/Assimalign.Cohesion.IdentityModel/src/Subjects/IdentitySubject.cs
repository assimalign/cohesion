using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an immutable canonical identity subject materialized from an
/// <see cref="IdentitySubjectDescriptor" />.
/// </summary>
/// <remarks>
/// This is deliberately a single sealed type rather than a per-kind type family:
/// <see cref="IdentityKind" /> is additive-only, and protocol normalizers frequently cannot
/// determine a kind at the sites where subjects are constructed (actor entries, delegation
/// records), so kind is data, not type identity.
/// </remarks>
public sealed class IdentitySubject : IIdentitySubject
{
    /// <summary>
    /// The maximum depth of an actor delegation chain accepted at materialization. Chains
    /// sourced from untrusted protocol data are bounded so chain walks always terminate.
    /// </summary>
    public const int MaxActorDepth = 32;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentitySubject" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The subject contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no primary identifier, or when the actor chain exceeds
    /// <see cref="MaxActorDepth" />.
    /// </exception>
    public IdentitySubject(IdentitySubjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Identifier is null)
        {
            throw new IdentityModelException(
                "An identity subject requires a primary identifier.");
        }

        ValidateActorChain(descriptor.Actor);

        Kind = descriptor.Kind;
        Identifier = descriptor.Identifier;
        Identifiers = CollectIdentifiers(descriptor.Identifier, descriptor.AdditionalIdentifiers);
        DisplayName = descriptor.DisplayName;
        Claims = new IdentityClaimCollection(descriptor.Claims);
        Actor = descriptor.Actor;
    }

    /// <inheritdoc />
    public IdentityKind Kind { get; }

    /// <inheritdoc />
    public SubjectIdentifier Identifier { get; }

    /// <inheritdoc />
    public IReadOnlyList<SubjectIdentifier> Identifiers { get; }

    /// <inheritdoc />
    public string? DisplayName { get; }

    /// <inheritdoc />
    public IIdentityClaimCollection Claims { get; }

    /// <inheritdoc />
    public IIdentitySubject? Actor { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Identifier.Value} ({Kind})";

    private static IReadOnlyList<SubjectIdentifier> CollectIdentifiers(
        SubjectIdentifier primary,
        IList<SubjectIdentifier> additional)
    {
        ArgumentNullException.ThrowIfNull(additional);

        var identifiers = new List<SubjectIdentifier> { primary };

        for (var index = 0; index < additional.Count; index++)
        {
            var identifier = additional[index];
            ArgumentNullException.ThrowIfNull(identifier, nameof(additional));

            if (!identifiers.Contains(identifier))
            {
                identifiers.Add(identifier);
            }
        }

        return new ReadOnlyCollection<SubjectIdentifier>(identifiers.ToArray());
    }

    private static void ValidateActorChain(IIdentitySubject? actor)
    {
        var depth = 0;
        for (var current = actor; current is not null; current = current.Actor)
        {
            if (++depth > MaxActorDepth)
            {
                throw new IdentityModelException(
                    $"The actor chain exceeds the maximum depth of {MaxActorDepth}.");
            }
        }
    }
}
