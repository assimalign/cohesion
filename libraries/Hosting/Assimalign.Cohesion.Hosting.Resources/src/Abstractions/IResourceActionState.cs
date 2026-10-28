using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The state a stateful action's writes change, read and written as the snapshot a gateway keeps
/// until the resource is next deployed.
/// </summary>
/// <remarks>
/// A snapshot is the state's whole value, written by the control plane's content serializer.
/// Restoring one applies that value whatever the instance's current state, so a restore can be
/// repeated, and restores of different actions can run in any order.
/// </remarks>
public interface IResourceActionState
{
    /// <summary>
    /// Writes the state's current value as a snapshot.
    /// </summary>
    /// <param name="serializer">The control plane's content serializer.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The serializer does not know the state's type.</exception>
    ReadOnlyMemory<byte> GetSnapshot(IResourceContentSerializer serializer);

    /// <summary>
    /// Applies a snapshot to the instance and makes it the state's current value.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="serializer">The control plane's content serializer.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>A task that completes when the snapshot is in effect.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ResourceContentException">
    /// The snapshot is not a value of the state's type, or the instance refuses the value.
    /// </exception>
    ValueTask RestoreAsync(ReadOnlyMemory<byte> snapshot, IResourceContentSerializer serializer, CancellationToken cancellationToken = default);
}
