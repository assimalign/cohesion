using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an immutable named multi-value attribute, the attribute-shaped counterpart to
/// claims. Protocol branches construct attributes explicitly (for example from a SAML
/// attribute statement); the canonical model does not derive attributes from claims, because
/// grouping loses per-claim provenance.
/// </summary>
/// <remarks>
/// An attribute with an empty <see cref="Values" /> list is legal and distinct from an absent
/// attribute (SAML permits declaring an attribute without disclosing values) and from an
/// attribute whose value is explicitly <see cref="IdentityClaimValue.Null" />.
/// </remarks>
public sealed class IdentityAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityAttribute" /> class by
    /// snapshotting the provided values.
    /// </summary>
    /// <param name="name">The attribute name.</param>
    /// <param name="values">The attribute values, in order. May be empty; the sequence is copied.</param>
    /// <param name="nameFormat">The attribute name format (for example a SAML <c>NameFormat</c> URI).</param>
    /// <param name="friendlyName">The human-readable attribute name (for example a SAML <c>FriendlyName</c>).</param>
    /// <param name="provenance">The protocol provenance of the attribute.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name" /> is null or whitespace, or when a value is undefined.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values" /> is null.</exception>
    public IdentityAttribute(
        string name,
        IEnumerable<IdentityClaimValue> values,
        string? nameFormat = null,
        string? friendlyName = null,
        IdentityClaimProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);

        var snapshot = new List<IdentityClaimValue>(values is IReadOnlyCollection<IdentityClaimValue> sized ? sized.Count : 4);
        foreach (var value in values)
        {
            if (value.IsUndefined)
            {
                throw new ArgumentException(
                    $"The attribute '{name}' must not contain undefined values. Use IdentityClaimValue.Null for explicit null.",
                    nameof(values));
            }

            snapshot.Add(value);
        }

        Name = name;
        Values = new ReadOnlyCollection<IdentityClaimValue>(snapshot.ToArray());
        NameFormat = nameFormat;
        FriendlyName = friendlyName;
        Provenance = provenance;
    }

    /// <summary>
    /// Gets the attribute name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the attribute values, in order. May be empty.
    /// </summary>
    public IReadOnlyList<IdentityClaimValue> Values { get; }

    /// <summary>
    /// Gets the attribute name format (for example a SAML <c>NameFormat</c> URI).
    /// </summary>
    public string? NameFormat { get; }

    /// <summary>
    /// Gets the human-readable attribute name (for example a SAML <c>FriendlyName</c>).
    /// </summary>
    public string? FriendlyName { get; }

    /// <summary>
    /// Gets the protocol provenance of the attribute.
    /// </summary>
    public IdentityClaimProvenance? Provenance { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Values.Count} values)";
}
