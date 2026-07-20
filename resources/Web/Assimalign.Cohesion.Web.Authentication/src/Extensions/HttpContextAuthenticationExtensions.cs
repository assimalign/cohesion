using System;
using System.Security.Claims;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Internal;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Surfaces the authenticated principal on <see cref="IHttpContext"/>
/// as a property, backed by <see cref="IAuthenticationFeature"/>
/// stored in the context's feature collection.
/// </summary>
/// <remarks>
/// The protocol core (<c>Assimalign.Cohesion.Http</c>) deliberately
/// omits identity — it's an application-layer concern, not part of the
/// wire protocol. This package brings back the familiar property-style
/// access (<c>context.User</c>) without forcing the protocol core to
/// depend on <c>System.Security.Claims</c>.
/// </remarks>
public static class HttpContextAuthenticationExtensions
{
    private static readonly ClaimsPrincipal EmptyPrincipal = new(new ClaimsIdentity());

    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets or sets the authenticated <see cref="ClaimsPrincipal"/>
        /// for the current exchange. Returns an empty principal (no
        /// claims, no identity) when no authentication middleware has
        /// attached an <see cref="IAuthenticationFeature"/> —
        /// matching the ASP.NET Core <c>HttpContext.User</c> default so
        /// callers don't need a null-check on the common path.
        /// </summary>
        public ClaimsPrincipal User
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IAuthenticationFeature>()?.User
                    ?? EmptyPrincipal;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(context);
                ArgumentNullException.ThrowIfNull(value);

                IAuthenticationFeature? feature = context.Features.Get<IAuthenticationFeature>();
                if (feature is null)
                {
                    context.Features.Set<IAuthenticationFeature>(new AuthenticationFeature(value));
                }
                else
                {
                    feature.User = value;
                }
            }
        }
    }
}
