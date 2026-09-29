using System;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

internal sealed class GatewayCommandDispatcher : IResourceCommandDispatcher
{
    private readonly IGatewayResourceCommandClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="GatewayCommandDispatcher"/> class.
    /// </summary>
    /// <param name="client">The gateway resource command client that delivers commands to resources of its resource kind.</param>
    public GatewayCommandDispatcher(IGatewayResourceCommandClient client)
    {
        _client = client;
    }

    public string ResourceKind => _client.ResourceKind;

    public async ValueTask<ReadOnlyMemory<byte>> ApplyAsync(
        Uri address, string bearerToken, ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default)
    {
        ResourceCommandResult result = await _client.ApplyAsync(address, bearerToken, command, serverCertificateValidator, cancellationToken).ConfigureAwait(false);
        if (result.Status != ResourceCommandStatus.Applied)
        {
            throw new ResourceCommandRejectedException(result.Detail);
        }
        return result.Result;
    }

    public async ValueTask DeleteAsync(
        Uri address, string bearerToken, ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default)
    {
        ResourceCommandResult result = await _client.DeleteAsync(address, bearerToken, command, serverCertificateValidator, cancellationToken).ConfigureAwait(false);
        if (result.Status != ResourceCommandStatus.Applied)
        {
            throw new ResourceCommandRejectedException(result.Detail);
        }
    }
}
