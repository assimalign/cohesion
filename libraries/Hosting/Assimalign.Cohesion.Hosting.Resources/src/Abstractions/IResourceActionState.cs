using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The state a stateful action's writes change, read and written as the snapshot a gateway keeps
/// until the resource is next deployed.
/// </summary>
/// <remarks>
/// A snapshot is the state's whole value as UTF-8 JSON. Restoring one applies that value whatever
/// the instance's current state, so a restore can be repeated, and restores of different actions
/// can run in any order.
/// </remarks>
public interface IResourceActionState
{
    /// <summary>
    /// Gets the state's current value as UTF-8 JSON.
    /// </summary>
    /// <returns>The snapshot.</returns>
    ReadOnlyMemory<byte> GetSnapshot();

    /// <summary>
    /// Applies a snapshot to the instance and makes it the state's current value.
    /// </summary>
    /// <param name="snapshot">The snapshot, as UTF-8 JSON.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>A task that completes when the snapshot is in effect.</returns>
    /// <exception cref="System.Text.Json.JsonException">The snapshot is not a value of the state's type.</exception>
    ValueTask RestoreAsync(ReadOnlyMemory<byte> snapshot, CancellationToken cancellationToken = default);
}
