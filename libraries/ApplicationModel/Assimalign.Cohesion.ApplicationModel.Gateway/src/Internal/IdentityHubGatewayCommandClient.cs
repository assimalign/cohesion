using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.IdentityHub.Client;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;
using ResourceCommand = Assimalign.Cohesion.IdentityHub.Client.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class IdentityHubGatewayCommandClient : IGatewayResourceCommandClient
{
    public string ResourceKind => "IdentityHub";

    public async ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, HostCommand command,
        CancellationToken cancellationToken = default)
    {
        using var client = IdentityHubCommandClient.Create(address, bearerToken);
        var observation = await client.SendCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    public async ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, HostCommand command,
        CancellationToken cancellationToken = default)
    {
        using var client = IdentityHubCommandClient.Create(address, bearerToken);
        var observation = await client.DeleteCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    private static ResourceCommand Convert(HostCommand command) =>
        new(command.Id, command.Kind, command.Owner, command.Key, command.Payload);
}
