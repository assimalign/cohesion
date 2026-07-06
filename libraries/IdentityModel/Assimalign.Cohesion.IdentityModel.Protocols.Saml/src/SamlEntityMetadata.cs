using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>EntityDescriptor</c>: a published entity's role descriptors,
/// keys, and endpoints. The typed <see cref="RoleDescriptors" /> are the authoritative
/// per-role view; the inherited base <see cref="ProtocolMetadata.Endpoints" /> and
/// <see cref="ProtocolMetadata.Keys" /> are the flat projection, with every entry stamped
/// with its enclosing role so a dual-role entity (one that is both identity provider and
/// service provider) stays unambiguous to protocol-neutral consumers.
/// </summary>
/// <remarks>
/// Materialization always stamps <see cref="ProtocolEndpoint.Role" /> and
/// <see cref="ProtocolKey.Role" /> from the enclosing role descriptor, overwriting whatever
/// the descriptor carried, so a role-scoped entity never projects a null-role endpoint or
/// key. Spec conformance (required role descriptors, well-formed endpoints, protocol
/// support enumeration) is reported by <see cref="Validate" /> rather than enforced at
/// materialization, so a compliance suite can hold and diagnose a non-conformant document.
/// </remarks>
public sealed class SamlEntityMetadata : ProtocolMetadata
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlEntityMetadata" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The entity metadata contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a NameID format entry is null or whitespace, or when a property name is
    /// blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no <c>entityID</c>, contains a null role descriptor,
    /// or a role descriptor contains a null endpoint or key.
    /// </exception>
    public SamlEntityMetadata(SamlEntityMetadataDescriptor descriptor)
        : base(PrepareBase(descriptor), AuthenticationProtocol.Saml2)
    {
        RoleDescriptors = SnapshotRoleDescriptors(descriptor.RoleDescriptors);
        Organization = descriptor.Organization;
        ContactPersons = SnapshotContacts(descriptor.ContactPersons);
    }

    /// <summary>
    /// Gets the SAML <c>entityID</c>. Alias of <see cref="ProtocolMetadata.EntityId" />.
    /// </summary>
    public string EntityIdentifier => EntityId;

    /// <summary>
    /// Gets the role descriptors the entity publishes.
    /// </summary>
    public IReadOnlyList<SamlRoleDescriptor> RoleDescriptors { get; }

    /// <summary>
    /// Gets the entity organization, when published.
    /// </summary>
    public SamlOrganization? Organization { get; }

    /// <summary>
    /// Gets the entity contact persons.
    /// </summary>
    public IReadOnlyList<SamlContactPerson> ContactPersons { get; }

    /// <summary>
    /// Gets the first role descriptor for the given role, or <see langword="null" /> when
    /// the entity does not publish that role.
    /// </summary>
    /// <param name="role">The role to look up.</param>
    /// <returns>The matching role descriptor, or <see langword="null" />.</returns>
    public SamlRoleDescriptor? GetRoleDescriptor(ProtocolRole role)
    {
        foreach (var descriptor in RoleDescriptors)
        {
            if (descriptor.Role == role)
            {
                return descriptor;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates the entity descriptor against the SAML 2.0 metadata conformance rules an
    /// entity descriptor can be checked for without its signature: it publishes at least one
    /// role descriptor, every role descriptor names a known role and advertises the SAML 2.0
    /// protocol support enumeration, and the metadata has not expired at
    /// <paramref name="validateAt" /> when a <c>validUntil</c> was published.
    /// </summary>
    /// <param name="validateAt">The instant to evaluate <c>validUntil</c> expiry against, when known.</param>
    /// <returns>The validation findings.</returns>
    public ProtocolValidationResult Validate(DateTimeOffset? validateAt = null)
    {
        var diagnostics = new List<ProtocolValidationDiagnostic>();

        if (RoleDescriptors.Count == 0)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.RoleDescriptorMissing,
                "A SAML entity descriptor must publish at least one role descriptor.",
                member: "RoleDescriptor"));
        }

        foreach (var descriptor in RoleDescriptors)
        {
            if (descriptor.Role == ProtocolRole.Unknown)
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Error,
                    ProtocolValidationCodes.ValueNotAllowed,
                    "A role descriptor must name a known role.",
                    member: "RoleDescriptor"));
            }

            if (!AdvertisesSaml2Protocol(descriptor.ProtocolSupportEnumeration))
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Warning,
                    ProtocolValidationCodes.MissingRecommendedMember,
                    "A role descriptor should advertise the SAML 2.0 protocol support enumeration.",
                    member: "protocolSupportEnumeration"));
            }
        }

        if (validateAt is { } instant && ValidUntil is { } expiry && instant > expiry)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.Expired,
                "The entity metadata has expired.",
                member: "validUntil"));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private static bool AdvertisesSaml2Protocol(string? protocolSupportEnumeration)
    {
        if (string.IsNullOrWhiteSpace(protocolSupportEnumeration))
        {
            return false;
        }

        foreach (var token in protocolSupportEnumeration.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, SamlConstants.ProtocolNamespace, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<SamlRoleDescriptor> SnapshotRoleDescriptors(IList<SamlRoleDescriptor> source)
    {
        if (source.Count == 0)
        {
            return Array.Empty<SamlRoleDescriptor>();
        }

        var snapshot = new SamlRoleDescriptor[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = source[index]
                ?? throw new IdentityModelException("A SAML entity descriptor must not contain null role descriptors.");
        }

        return new ReadOnlyCollection<SamlRoleDescriptor>(snapshot);
    }

    private static IReadOnlyList<SamlContactPerson> SnapshotContacts(IList<SamlContactPerson> source)
    {
        if (source.Count == 0)
        {
            return Array.Empty<SamlContactPerson>();
        }

        var snapshot = new SamlContactPerson[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = source[index]
                ?? throw new IdentityModelException("A SAML entity descriptor must not contain null contact persons.");
        }

        return new ReadOnlyCollection<SamlContactPerson>(snapshot);
    }

    private static ProtocolMetadataDescriptor PrepareBase(SamlEntityMetadataDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var merged = new MergedDescriptor
        {
            EntityId = descriptor.EntityId,
            ValidUntil = descriptor.ValidUntil,
            CacheDuration = descriptor.CacheDuration,
            RawDocument = descriptor.RawDocument,
        };

        foreach (var (name, value) in descriptor.Properties)
        {
            merged.Properties[name] = value;
        }

        // Any entity-wide endpoints/keys authored directly on the descriptor keep their own
        // (possibly null) role scope.
        foreach (var endpoint in descriptor.Endpoints)
        {
            merged.Endpoints.Add(endpoint
                ?? throw new IdentityModelException("A SAML entity descriptor must not contain null endpoints."));
        }

        foreach (var key in descriptor.Keys)
        {
            merged.Keys.Add(key
                ?? throw new IdentityModelException("A SAML entity descriptor must not contain null keys."));
        }

        foreach (var role in descriptor.Roles)
        {
            merged.Roles.Add(role);
        }

        foreach (var roleDescriptor in descriptor.RoleDescriptors)
        {
            if (roleDescriptor is null)
            {
                throw new IdentityModelException("A SAML entity descriptor must not contain null role descriptors.");
            }

            if (!merged.Roles.Contains(roleDescriptor.Role))
            {
                merged.Roles.Add(roleDescriptor.Role);
            }

            // The enclosing role descriptor is authoritative for role scope: stamp it onto
            // every projected endpoint and key so a role-scoped entity never surfaces a
            // null-role entry, regardless of what the caller authored.
            foreach (var endpoint in roleDescriptor.Endpoints)
            {
                merged.Endpoints.Add(StampRole(endpoint, roleDescriptor.Role));
            }

            foreach (var key in roleDescriptor.Keys)
            {
                merged.Keys.Add(StampRole(key, roleDescriptor.Role));
            }
        }

        return merged;
    }

    private static ProtocolEndpoint StampRole(ProtocolEndpoint endpoint, ProtocolRole role)
    {
        if (endpoint.Role == role)
        {
            return endpoint;
        }

        var rebuilt = new ProtocolEndpointDescriptor
        {
            Kind = endpoint.Kind,
            Location = endpoint.Location,
            ResponseLocation = endpoint.ResponseLocation,
            Binding = endpoint.Binding,
            Role = role,
            Index = endpoint.Index,
            IsDefault = endpoint.IsDefault,
        };

        foreach (var (name, value) in endpoint.Properties)
        {
            rebuilt.Properties[name] = value;
        }

        return new ProtocolEndpoint(rebuilt);
    }

    private static ProtocolKey StampRole(ProtocolKey key, ProtocolRole role)
    {
        if (key.Role == role)
        {
            return key;
        }

        var rebuilt = new ProtocolKeyDescriptor
        {
            Use = key.Use,
            KeyId = key.KeyId,
            Role = role,
        };

        foreach (var certificate in key.Certificates)
        {
            rebuilt.Certificates.Add(certificate);
        }

        foreach (var algorithm in key.Algorithms)
        {
            rebuilt.Algorithms.Add(algorithm);
        }

        foreach (var (name, value) in key.Properties)
        {
            rebuilt.Properties[name] = value;
        }

        return new ProtocolKey(rebuilt);
    }

    private sealed class MergedDescriptor : ProtocolMetadataDescriptor
    {
    }
}
