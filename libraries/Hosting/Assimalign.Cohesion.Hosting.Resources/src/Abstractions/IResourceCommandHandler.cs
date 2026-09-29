using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Implements an area-owned command through its runtime's mutation seams.</summary>
public interface IResourceCommandHandler
{
    /// <summary>Gets the accepted command kind handled by this instance.</summary>
    string Kind { get; }

    /// <summary>Applies a command after ownership and replay checks.</summary>
    /// <param name="command">The validated command envelope.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>The area-defined response bytes.</returns>
    /// <exception cref="ResourceCommandRejectedException">The runtime refuses the mutation.</exception>
    ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default);

    /// <summary>Removes the effect of the previously accepted command.</summary>
    /// <param name="command">The stored command owned by the requesting application.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>The area-defined response bytes.</returns>
    /// <exception cref="ResourceCommandRejectedException">The runtime refuses deletion.</exception>
    ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default);
}
