using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Describes the contents of an identity subject before it is materialized into an
/// immutable <see cref="IdentitySubject" />.
/// </summary>
public class IdentitySubjectDescriptor
{
    /// <summary>
    /// Gets or sets the kind of principal. Defaults to <see cref="IdentityKind.Unknown" />;
    /// normalizers set the kind only when the source data actually declares one.
    /// </summary>
    public IdentityKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the primary subject identifier. Required at materialization.
    /// </summary>
    public SubjectIdentifier? Identifier { get; set; }

    /// <summary>
    /// Gets the additional identifiers known for the subject (for example identifiers from
    /// federated providers). Duplicates of the primary identifier or of each other are
    /// removed at materialization, preserving first-occurrence order.
    /// </summary>
    public IList<SubjectIdentifier> AdditionalIdentifiers { get; } = new List<SubjectIdentifier>();

    /// <summary>
    /// Gets or sets the human-readable display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets the normalized claims asserted about the subject.
    /// </summary>
    public IList<IIdentityClaim> Claims { get; } = new List<IIdentityClaim>();

    /// <summary>
    /// Gets or sets the party acting as this subject. See
    /// <see cref="IIdentitySubject.Actor" /> for the chain semantics.
    /// </summary>
    public IIdentitySubject? Actor { get; set; }
}
