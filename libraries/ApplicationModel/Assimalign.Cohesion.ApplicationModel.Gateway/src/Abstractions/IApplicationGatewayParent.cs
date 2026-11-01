using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// A gateway as its child gateways see it: the parent a child joins, registering under a lease and
/// reading the outputs of the parent's resources that its settings use.
/// </summary>
/// <remarks>
/// <para>
/// A child joins when its run starts and joins again three times per lease to stay registered; the
/// parent drops a child whose lease lapses. Joining again is how a child renews, and every answer
/// carries the values the parent has recorded at that moment. The parent knows nothing of a child
/// before it joins, so each gateway deploys on its own.
/// </para>
/// <para>
/// <see cref="ApplicationGateway"/> implements it, so a child in the same process joins its parent
/// directly; a transport carries the call between processes.
/// </para>
/// </remarks>
public interface IApplicationGatewayParent
{
    /// <summary>
    /// Gets the parent gateway's name, which a child's manifest names as its parent.
    /// </summary>
    GatewayName Name { get; }

    /// <summary>
    /// Registers a child gateway, or renews its registration, and answers with the values it uses.
    /// </summary>
    /// <param name="child">The child's manifest: its name, this gateway as its parent, and its resources.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The values of this gateway's outputs that the child's settings use, as far as they are
    /// recorded, and the child's lease.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="child"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException">
    /// The manifest breaks a rule of <see cref="ApplicationModel.Create(ApplicationModelManifest)"/>, or
    /// names another gateway as its parent.
    /// </exception>
    /// <exception cref="ApplicationGatewayException">
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>).
    /// </exception>
    ValueTask<ApplicationGatewayMembership> JoinAsync(ApplicationModelManifest child, CancellationToken cancellationToken = default);
}
