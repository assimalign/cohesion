using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

internal sealed class GatewayCommandDispatcher(IGatewayResourceCommandClient client) : IResourceCommandDispatcher
{
    public string ResourceKind => client.ResourceKind;

    public async ValueTask<ReadOnlyMemory<byte>> ApplyAsync(
        Uri address, string bearerToken, ResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ResourceCommandResult result = await client.ApplyAsync(address, bearerToken, command, cancellationToken).ConfigureAwait(false);
        if (result.Status != ResourceCommandStatus.Applied)
        {
            throw new ResourceCommandRejectedException(result.Detail);
        }
        return result.Result;
    }

    public async ValueTask DeleteAsync(
        Uri address, string bearerToken, ResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ResourceCommandResult result = await client.DeleteAsync(address, bearerToken, command, cancellationToken).ConfigureAwait(false);
        if (result.Status != ResourceCommandStatus.Applied)
        {
            throw new ResourceCommandRejectedException(result.Detail);
        }
    }
}
