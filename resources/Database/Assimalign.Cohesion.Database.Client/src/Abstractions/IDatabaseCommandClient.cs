using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>Delivers declarative commands to the Database resource's admin control plane.</summary>
public interface IDatabaseCommandClient : IDisposable
{
    /// <summary>Applies a declaration and observes the provider's result.</summary>
    /// <param name="command">The command envelope.</param>
    /// <param name="cancellationToken">Cancels transport and response reading.</param>
    /// <returns>The observed status and provider detail.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">The endpoint cannot be reached.</exception>
    ValueTask<ResourceCommandObservation> SendCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes an owned declaration and observes the provider's result.</summary>
    /// <param name="command">The previously applied command envelope.</param>
    /// <param name="cancellationToken">Cancels transport and response reading.</param>
    /// <returns>The observed status and provider detail.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">The endpoint cannot be reached.</exception>
    ValueTask<ResourceCommandObservation> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default);
}
