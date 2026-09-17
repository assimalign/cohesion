using System;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;
using ResourceCommand = Assimalign.Cohesion.Database.Client.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class DatabaseGatewayCommandClient : IGatewayResourceCommandClient
{
    public string ResourceKind => "Database";

    public async ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, HostCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default)
    {
        using HttpMessageInvoker transport = GatewayHttpTransport.Create(serverCertificateValidator);
        using IDatabaseCommandClient client = DatabaseCommandClient.Create(address, bearerToken, transport);
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
        using IDatabaseCommandClient client = DatabaseCommandClient.Create(address, bearerToken, transport);
        var observation = await client.DeleteCommandAsync(Convert(command), cancellationToken).ConfigureAwait(false);
        return new ResourceCommandResult(
            observation.Status is "Applied" or "Deleted" ? ResourceCommandStatus.Applied : ResourceCommandStatus.Rejected,
            observation.Detail ?? observation.Status);
    }

    private static ResourceCommand Convert(HostCommand command) =>
        new(command.Id, command.Kind, command.Owner, command.Key, command.Payload);
}
