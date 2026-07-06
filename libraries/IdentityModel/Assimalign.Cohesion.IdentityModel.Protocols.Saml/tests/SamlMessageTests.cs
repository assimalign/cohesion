using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml.Tests;

/// <summary>
/// Verifies the SAML protocol message envelopes: the <c>AuthnRequest</c> structural rules
/// (SAML Core §3.4.1) and the <c>Response</c> envelope validation (SAML Core §3.3.3).
/// </summary>
public sealed class SamlMessageTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A well-formed authentication request should validate")]
    public void AuthnRequest_WhenWellFormed_ShouldValidate()
    {
        var request = new SamlAuthnRequest(new SamlAuthnRequestDescriptor
        {
            MessageId = "_req-1",
            Issuer = SamlAssertionFixtures.RelyingParty,
            Destination = "https://idp.example.com/sso",
            AssertionConsumerServiceUrl = SamlAssertionFixtures.AcsUrl,
            NameIdPolicyFormat = SamlNameIdFormats.EmailAddress,
        });

        request.Validate().Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A request must not set both an ACS URL and index")]
    public void AuthnRequest_WhenAcsUrlAndIndexBothSet_ShouldReportRequestParametersInvalid()
    {
        var request = new SamlAuthnRequest(new SamlAuthnRequestDescriptor
        {
            MessageId = "_req-1",
            AssertionConsumerServiceUrl = SamlAssertionFixtures.AcsUrl,
            AssertionConsumerServiceIndex = 2,
        });

        var result = request.Validate();

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.RequestParametersInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A request must not combine ForceAuthn with IsPassive")]
    public void AuthnRequest_WhenForceAuthnAndPassive_ShouldReportRequestParametersInvalid()
    {
        var request = new SamlAuthnRequest(new SamlAuthnRequestDescriptor
        {
            MessageId = "_req-1",
            AssertionConsumerServiceIndex = 0,
            ForceAuthn = true,
            IsPassive = true,
        });

        var result = request.Validate();

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.RequestParametersInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A request requires a message identifier")]
    public void AuthnRequest_WhenNoMessageId_ShouldThrow()
    {
        Should.Throw<IdentityModelException>(() => new SamlAuthnRequest(new SamlAuthnRequestDescriptor
        {
            AssertionConsumerServiceUrl = SamlAssertionFixtures.AcsUrl,
        }));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A conformant success response should validate")]
    public void Response_WhenConformant_ShouldValidate()
    {
        var response = ConformantResponse();

        var result = response.Validate(new SamlResponseValidationOptions
        {
            ExpectedInResponseTo = SamlAssertionFixtures.RequestId,
            ExpectedDestination = SamlAssertionFixtures.AcsUrl,
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A failure status response should fail")]
    public void Response_WhenStatusNotSuccess_ShouldReportStatusNotSuccess()
    {
        var descriptor = new SamlResponseDescriptor
        {
            MessageId = "_resp-1",
            Issuer = SamlAssertionFixtures.Issuer,
            Destination = SamlAssertionFixtures.AcsUrl,
            InResponseTo = SamlAssertionFixtures.RequestId,
            Status = ProtocolResponseStatus.Failed(SamlStatusCodes.Requester, subCodes: new[] { SamlStatusCodes.RequestDenied }),
        };
        var response = new SamlResponse(descriptor);

        var result = response.Validate(new SamlResponseValidationOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.StatusNotSuccess);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A mismatched InResponseTo should fail")]
    public void Response_WhenInResponseToMismatched_ShouldReportInResponseToMismatch()
    {
        var response = ConformantResponse();

        var result = response.Validate(new SamlResponseValidationOptions
        {
            ExpectedInResponseTo = "_a-different-request",
        });

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.InResponseToMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A mismatched Destination should fail")]
    public void Response_WhenDestinationMismatched_ShouldReportDestinationMismatch()
    {
        var response = ConformantResponse();

        var result = response.Validate(new SamlResponseValidationOptions
        {
            ExpectedDestination = "https://sp.example.com/wrong-acs",
        });

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.DestinationMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A success response with no assertion should fail")]
    public void Response_WhenSuccessButNoAssertion_ShouldReportAssertionMissing()
    {
        var descriptor = new SamlResponseDescriptor
        {
            MessageId = "_resp-1",
            Issuer = SamlAssertionFixtures.Issuer,
            Status = ProtocolResponseStatus.SuccessWith(SamlStatusCodes.Success),
        };
        var response = new SamlResponse(descriptor);

        var result = response.Validate(new SamlResponseValidationOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.AssertionMissing);
    }

    private static SamlResponse ConformantResponse()
    {
        var descriptor = new SamlResponseDescriptor
        {
            MessageId = "_resp-1",
            Issuer = SamlAssertionFixtures.Issuer,
            Destination = SamlAssertionFixtures.AcsUrl,
            InResponseTo = SamlAssertionFixtures.RequestId,
            Status = ProtocolResponseStatus.SuccessWith(SamlStatusCodes.Success),
        };
        descriptor.Assertions.Add(new SamlAssertion(SamlAssertionFixtures.ConformantAssertion()));
        return new SamlResponse(descriptor);
    }
}
