using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Describes the contents of a SAML <c>EntityDescriptor</c> before it is materialized into
/// an immutable <see cref="SamlEntityMetadata" />.
/// </summary>
/// <remarks>
/// The role descriptors are the primary input. Materialization projects each role
/// descriptor's endpoints and keys into the inherited base endpoint and key lists, so
/// protocol-neutral consumers can enumerate them while the typed role descriptors carry the
/// SAML-specific policy flags and NameID formats the base cannot hold. Each projected
/// endpoint and key keeps its role scope, so a dual-role entity's identity-provider and
/// service-provider keys stay distinguishable.
/// </remarks>
public class SamlEntityMetadataDescriptor : ProtocolMetadataDescriptor
{
    /// <summary>
    /// Gets the role descriptors the entity publishes. Set the SAML <c>entityID</c> through
    /// the inherited <see cref="ProtocolMetadataDescriptor.EntityId" />.
    /// </summary>
    public IList<SamlRoleDescriptor> RoleDescriptors { get; } = new List<SamlRoleDescriptor>();

    /// <summary>
    /// Gets or sets the entity organization.
    /// </summary>
    public SamlOrganization? Organization { get; set; }

    /// <summary>
    /// Gets the entity contact persons.
    /// </summary>
    public IList<SamlContactPerson> ContactPersons { get; } = new List<SamlContactPerson>();
}
