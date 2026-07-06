using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Tests;

/// <summary>
/// Contains unit tests for the two-leg protocol exchange description.
/// </summary>
public sealed class ProtocolExchangeTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Front-channel exchanges should describe both legs")]
    public void Constructor_WhenFrontChannel_ShouldDescribeBothLegs()
    {
        // OIDC authorization code flow: request to the authorization endpoint via
        // redirect, response delivered to the client's redirect URI via form_post.
        var exchange = new ProtocolExchange(
            requestEndpoint: new ProtocolEndpoint(new ProtocolEndpointDescriptor
            {
                Kind = new ProtocolEndpointKind("authorization"),
                Location = "https://op.example/authorize",
                Binding = ProtocolBinding.HttpRedirect,
            }),
            responseEndpoint: new ProtocolEndpoint(new ProtocolEndpointDescriptor
            {
                Kind = new ProtocolEndpointKind("redirect"),
                Location = "https://client.example/cb",
                Binding = ProtocolBinding.HttpPost,
            }));

        exchange.RequestEndpoint.Binding.ShouldBe(ProtocolBinding.HttpRedirect);
        exchange.ResponseEndpoint.ShouldNotBeNull();
        exchange.ResponseEndpoint!.Binding.ShouldBe(ProtocolBinding.HttpPost);
        exchange.ResponseEndpoint.Location.ShouldBe("https://client.example/cb");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Back-channel exchanges should have no response endpoint")]
    public void Constructor_WhenBackChannel_ShouldHaveNoResponseEndpoint()
    {
        // Token endpoint: the response returns on the same connection.
        var exchange = new ProtocolExchange(new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Kind = new ProtocolEndpointKind("token"),
            Location = "https://op.example/token",
            Binding = ProtocolBinding.BackChannel,
        }));

        exchange.ResponseEndpoint.ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => new ProtocolExchange(null!));
    }
}
