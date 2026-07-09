using System.Collections.Generic;
using System.Security.Claims;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Projects a validated <see cref="JsonWebToken"/> onto a <see cref="ClaimsPrincipal"/>, bridging
/// the IdentityModel claim model to the Web layer's <c>System.Security.Claims</c> vocabulary. This
/// is the only place the two claim models meet; the reflection-free projection keeps the bearer
/// handler AOT-safe.
/// </summary>
internal static class JwtClaimsPrincipalMapper
{
    public static ClaimsPrincipal Map(JsonWebToken token, string authenticationType, JwtBearerOptions options)
    {
        string defaultIssuer = options.ClaimsIssuer ?? token.Issuer ?? ClaimsIdentity.DefaultIssuer;
        var claims = new List<Claim>(token.Claims.Count);

        foreach (IIdentityClaim claim in token.Claims)
        {
            string issuer = claim.Issuer ?? defaultIssuer;
            IdentityClaimValue value = claim.Value;

            // Array-valued claims (for example a roles array) expand to one Claim per element so
            // ClaimsPrincipal.IsInRole and multi-value reads behave as callers expect.
            if (value.Kind == IdentityValueKind.Array)
            {
                foreach (IdentityClaimValue element in value.AsArray())
                {
                    AddClaim(claims, claim.Type, element, issuer);
                }
            }
            else
            {
                AddClaim(claims, claim.Type, value, issuer);
            }
        }

        ClaimsIdentity identity = new(claims, authenticationType, options.NameClaimType, options.RoleClaimType);
        return new ClaimsPrincipal(identity);
    }

    private static void AddClaim(List<Claim> claims, string type, IdentityClaimValue value, string issuer)
    {
        if (value.IsNull || value.IsUndefined)
        {
            return;
        }

        claims.Add(new Claim(type, value.ToString(), MapValueType(value.Kind), issuer));
    }

    private static string MapValueType(IdentityValueKind kind) => kind switch
    {
        IdentityValueKind.Integer => ClaimValueTypes.Integer64,
        IdentityValueKind.Double => ClaimValueTypes.Double,
        IdentityValueKind.Boolean => ClaimValueTypes.Boolean,
        IdentityValueKind.DateTime => ClaimValueTypes.DateTime,
        IdentityValueKind.Binary => ClaimValueTypes.Base64Binary,
        _ => ClaimValueTypes.String,
    };
}
