using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.SecretStore.Client;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;
using ResourceCommand = Assimalign.Cohesion.SecretStore.Client.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class SecretStoreGatewayCommandClient : IGatewayResourceCommandClient
{
    public string ResourceKind => "SecretStore";

    public async ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, HostCommand command,
        CancellationToken cancellationToken = default)
    {
        var client = SecretStoreClient.CreateForControlPlane(address, new ClientCredential(bearerToken));
        var observation = await client.ObserveCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    public async ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, HostCommand command,
        CancellationToken cancellationToken = default)
    {
        var client = SecretStoreClient.CreateForControlPlane(address, new ClientCredential(bearerToken));
        var observation = await client.DeleteCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    private static ResourceCommand Convert(HostCommand command) =>
        new(command.Id, command.Kind, command.Owner, command.Key, command.Payload);
}
