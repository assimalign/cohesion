using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// A way a gateway's instances reach it: it receives the manifests they publish and their renewals,
/// and hands each to the gateway. An HTTP endpoint is one; a channel inside the gateway's process,
/// for resources the gateway runs there, is another.
/// </summary>
/// <remarks>
/// <para>
/// The gateway itself is transport-neutral: it takes each publication and renewal through
/// <see cref="IApplicationInstancePublisher"/>. A transport carries them to it from wherever its
/// instances run, and an instance's own publisher (the one its
/// <see cref="AppEnvironment.Variables.ManifestEndpoint"/> variable names) carries them to the transport.
/// </para>
/// <para>
/// A gateway starts each of its transports when its run has loaded its record, before it joins its
/// parent or deploys anything, so an instance that survived the previous run can publish again; it
/// stops them when the run ends. A transport must not hand anything to the gateway once it has
/// stopped.
/// </para>
/// </remarks>
public interface IApplicationGatewayTransport
{
    /// <summary>
    /// Starts handing the publications and renewals the transport receives to the gateway.
    /// </summary>
    /// <param name="gateway">The gateway that receives them.</param>
    /// <param name="cancellationToken">Cancels starting.</param>
    /// <returns>A task that completes when the transport receives.</returns>
    ValueTask StartAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops handing anything to the gateway: a publication or renewal received from here is refused.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for a publication in flight.</param>
    /// <returns>A task that completes when the transport has stopped.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
