using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Guards the deliberate claim-name mirrors across independent family branches. The JSON Web
/// Token package (token branch) cannot reference the OpenID Connect package (protocol branch),
/// so each owns its own copy of the IANA-registered JWT claim names it materializes. This test
/// pins the copies equal so the two vocabularies cannot silently drift.
/// </summary>
public sealed class IdentityModelClaimVocabularyTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Vocabulary: JWT and OIDC claim names should match")]
    public void JwtAndOpenIdConnectClaimNames_ShouldMatch()
    {
        JsonWebTokenClaimTypes.Nonce.ShouldBe(OpenIdConnectClaimTypes.Nonce);
        JsonWebTokenClaimTypes.AuthorizedParty.ShouldBe(OpenIdConnectClaimTypes.Azp);
        JsonWebTokenClaimTypes.AuthTime.ShouldBe(OpenIdConnectClaimTypes.AuthTime);
        JsonWebTokenClaimTypes.AccessTokenHash.ShouldBe(OpenIdConnectClaimTypes.AccessTokenHash);
        JsonWebTokenClaimTypes.CodeHash.ShouldBe(OpenIdConnectClaimTypes.CodeHash);
        JsonWebTokenClaimTypes.AuthenticationContextClassReference.ShouldBe(OpenIdConnectClaimTypes.Acr);
        JsonWebTokenClaimTypes.AuthenticationMethodReferences.ShouldBe(OpenIdConnectClaimTypes.Amr);
        JsonWebTokenClaimTypes.SessionId.ShouldBe(OpenIdConnectClaimTypes.SessionId);
    }
}
