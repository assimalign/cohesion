using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Persists a gateway's record of what it deployed and recorded, so a restarted gateway reconciles
/// against it instead of starting over.
/// </summary>
/// <remarks>
/// The gateway loads its record once, when it starts, and saves the whole record after every change:
/// a deployment, a registered instance, a recorded write, a teardown. Saves for one gateway never
/// overlap. <see cref="JsonFileApplicationGatewayStore"/> keeps one JSON file per gateway; a store over a
/// database replaces it without changing the gateway.
/// </remarks>
public interface IApplicationGatewayStore
{
    /// <summary>
    /// Loads a gateway's record.
    /// </summary>
    /// <param name="gateway">The gateway's name.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The record, or <see langword="null"/> when the gateway has none because it never deployed.</returns>
    ValueTask<ApplicationGatewayRecord?> LoadAsync(GatewayName gateway, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a gateway's record, replacing the previous one.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the record is stored.</returns>
    ValueTask SaveAsync(ApplicationGatewayRecord record, CancellationToken cancellationToken = default);
}
