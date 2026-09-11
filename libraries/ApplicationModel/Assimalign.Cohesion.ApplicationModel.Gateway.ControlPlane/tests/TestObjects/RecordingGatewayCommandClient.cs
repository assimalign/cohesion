using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

internal sealed class RecordingGatewayCommandClient : IGatewayResourceCommandClient
{
    public string ResourceKind => "test";

    public int Applied { get; private set; }

    public int Deleted { get; private set; }

    public bool RejectDeletion { get; set; }

    public string ExpectedOwner { get; set; } = "caller";

    public ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, ResourceCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        address.ShouldBe(new Uri("http://127.0.0.1:43110/cohesion/v1"));
        JsonWebToken.Parse(bearerToken).Audiences.ShouldContain("api");
        command.Owner.ShouldBe(ExpectedOwner);
        Applied++;
        return ValueTask.FromResult(command.Key == "reject"
            ? new ResourceCommandResult(ResourceCommandStatus.Rejected, "The provider refused the requested setting.")
            : new ResourceCommandResult(ResourceCommandStatus.Applied, "accepted", "accepted"u8.ToArray()));
    }

    public ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, ResourceCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        address.ShouldBe(new Uri("http://127.0.0.1:43110/cohesion/v1"));
        JsonWebToken.Parse(bearerToken).Audiences.ShouldContain("api");
        command.Owner.ShouldBe(ExpectedOwner);
        Deleted++;
        return ValueTask.FromResult(RejectDeletion
            ? new ResourceCommandResult(ResourceCommandStatus.Rejected, "The provider could not remove the setting.")
            : new ResourceCommandResult(ResourceCommandStatus.Applied, "removed"));
    }
}
