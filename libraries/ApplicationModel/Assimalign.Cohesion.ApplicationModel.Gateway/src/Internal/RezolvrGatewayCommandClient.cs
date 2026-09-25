using System;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Rezolvr.Client;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;
using ResourceCommand = Assimalign.Cohesion.Rezolvr.Client.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed class RezolvrGatewayCommandClient : IGatewayResourceCommandClient
{
    public string ResourceKind => "Rezolvr";

    public async ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, HostCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default)
    {
        using HttpMessageInvoker transport = GatewayHttpTransport.Create(serverCertificateValidator);
        using var client = RezolvrCommandClient.Create(address, bearerToken, transport);
        var observation = await client.SendCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    public async ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, HostCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default)
    {
        using HttpMessageInvoker transport = GatewayHttpTransport.Create(serverCertificateValidator);
        using var client = RezolvrCommandClient.Create(address, bearerToken, transport);
        var observation = await client.DeleteCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    private static ResourceCommand Convert(HostCommand command) =>
        new(command.Id, command.Kind, command.Owner, command.Key, command.Payload);
}
