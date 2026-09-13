using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Rezolvr.Client;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;
using ResourceCommand = Assimalign.Cohesion.Rezolvr.Client.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class RezolvrGatewayCommandClient : IGatewayResourceCommandClient
{
    public string ResourceKind => "Rezolvr";

    public async ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, HostCommand command,
        CancellationToken cancellationToken = default)
    {
        using var client = RezolvrCommandClient.Create(address, bearerToken);
        var observation = await client.SendCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    public async ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, HostCommand command,
        CancellationToken cancellationToken = default)
    {
        using var client = RezolvrCommandClient.Create(address, bearerToken);
        var observation = await client.DeleteCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    private static ResourceCommand Convert(HostCommand command) =>
        new(command.Id, command.Kind, command.Owner, command.Key, command.Payload);
}
