namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The directory of an identity hub: the registries of its identity objects, each serving the
/// objects defined in code over the ones kept in the hub's store.
/// </summary>
/// <remarks>
/// <para>
/// A directory is what the rest of the hub reads identity objects through, so an object defined
/// in code and one created at run time are indistinguishable to a flow, a policy, or a
/// management API.
/// </para>
/// <para>
/// <see cref="IdentityHubDirectory"/> is the implementation, over one
/// <see cref="IIdentityHubStore"/>. A directory gains a registry with each kind of identity
/// object the hub learns.
/// </para>
/// </remarks>
public interface IIdentityHubDirectory
{
    /// <summary>
    /// Gets the directory's users.
    /// </summary>
    IIdentityUserRegistry Users { get; }

    /// <summary>
    /// Gets the directory's groups.
    /// </summary>
    IIdentityGroupRegistry Groups { get; }
}
