using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub.Client;

/// <summary>Delivers declarative commands to the IdentityHub resource's admin control plane.</summary>
public interface IIdentityHubCommandClient : IDisposable
{
    /// <summary>Applies a declaration and observes the provider's result.</summary>
    /// <param name="command">The command envelope.</param>
    /// <param name="cancellationToken">Cancels transport and response reading.</param>
    /// <returns>The observed status and provider detail.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">The endpoint cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">Delivery is canceled.</exception>
    /// <exception cref="System.Text.Json.JsonException">The endpoint returns malformed observation JSON.</exception>
    ValueTask<ResourceCommandObservation> SendCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes an owned declaration and observes the provider's result.</summary>
    /// <param name="command">The previously applied command envelope.</param>
    /// <param name="cancellationToken">Cancels transport and response reading.</param>
    /// <returns>The observed status and provider detail.</returns>
    /// <exception cref="ArgumentNullException">The command is null.</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">The endpoint cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">Delivery is canceled.</exception>
    /// <exception cref="System.Text.Json.JsonException">The endpoint returns malformed observation JSON.</exception>
    ValueTask<ResourceCommandObservation> DeleteCommandAsync(ResourceCommand command, CancellationToken cancellationToken = default);
}
