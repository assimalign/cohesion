using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.Tests.TestObjects;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Tests;

/// <summary>
/// Contains unit tests for the normalized response status and the fail-closed status
/// requirement on responses.
/// </summary>
public sealed class ProtocolResponseStatusTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Success may carry wire codes")]
    public void SuccessWith_WhenGivenWireDetail_ShouldRemainSucceeded()
    {
        // A SAML single-logout response can succeed while carrying PartialLogout — the
        // outcome is stored, never inferred from code presence.
        var partial = ProtocolResponseStatus.SuccessWith(
            code: "urn:oasis:names:tc:SAML:2.0:status:Success",
            subCodes: ["urn:oasis:names:tc:SAML:2.0:status:PartialLogout"],
            message: "Not all session participants were reached.");

        partial.Succeeded.ShouldBeTrue();
        partial.Code.ShouldBe("urn:oasis:names:tc:SAML:2.0:status:Success");
        partial.SubCodes.Count.ShouldBe(1);
        partial.Message.ShouldNotBeNull();

        ProtocolResponseStatus.Success.Succeeded.ShouldBeTrue();
        ProtocolResponseStatus.Success.Code.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Failed status should preserve the nested code chain")]
    public void Failed_WhenGivenNestedCodes_ShouldPreserveTheChainInOrder()
    {
        // SAML status nesting is unbounded; real products emit three levels.
        var failed = ProtocolResponseStatus.Failed(
            "urn:oasis:names:tc:SAML:2.0:status:Responder",
            message: "Authentication failed.",
            subCodes:
            [
                "urn:oasis:names:tc:SAML:2.0:status:AuthnFailed",
                "urn:vendor:status:MfaTimeout",
            ],
            detailUri: "https://idp.example/errors/mfa");

        failed.Succeeded.ShouldBeFalse();
        failed.SubCodes[0].ShouldBe("urn:oasis:names:tc:SAML:2.0:status:AuthnFailed");
        failed.SubCodes[1].ShouldBe("urn:vendor:status:MfaTimeout");
        failed.DetailUri.ShouldBe("https://idp.example/errors/mfa");

        Should.Throw<ArgumentException>(() => ProtocolResponseStatus.Failed(" "));
        Should.Throw<ArgumentException>(() => ProtocolResponseStatus.Failed("code", subCodes: [" "]));

        // A blank wire code is a malformation at any nesting depth, on either outcome.
        Should.Throw<ArgumentException>(() => ProtocolResponseStatus.SuccessWith(code: ""));
        Should.Throw<ArgumentException>(() => ProtocolResponseStatus.SuccessWith(subCodes: [" "]));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Responses should fail closed without a status")]
    public void Response_WhenStatusIsUnmapped_ShouldFailConstruction()
    {
        // Absence-means-success is a wire-parsing rule for branch materializers; a
        // response whose status was never mapped must not read as accepted.
        var unmapped = new TestResponseDescriptor { Issuer = "https://idp.example" };

        Should.Throw<IdentityModelException>(() => new TestResponse(unmapped, AuthenticationProtocol.Saml2));

        var explicitSuccess = new TestResponseDescriptor
        {
            Issuer = "https://op.example",
            Status = ProtocolResponseStatus.Success,
        };
        var response = new TestResponse(explicitSuccess, AuthenticationProtocol.OpenIdConnect);

        response.Status.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Response should carry InResponseTo correlation")]
    public void Response_WhenCorrelated_ShouldCarryInResponseTo()
    {
        var response = new TestResponse(new TestResponseDescriptor
        {
            MessageId = "_response-1",
            InResponseTo = "_request-1",
            Status = ProtocolResponseStatus.Success,
        }, AuthenticationProtocol.Saml2);

        response.InResponseTo.ShouldBe("_request-1");
    }
}
