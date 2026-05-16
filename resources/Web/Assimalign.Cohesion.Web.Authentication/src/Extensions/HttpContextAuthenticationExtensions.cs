using System;
using System.Security.Claims;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Surfaces the authenticated principal on <see cref="IHttpContext"/>
/// as a property, backed by <see cref="IHttpAuthenticationFeature"/>
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
        /// attached an <see cref="IHttpAuthenticationFeature"/> —
        /// matching the ASP.NET Core <c>HttpContext.User</c> default so
        /// callers don't need a null-check on the common path.
        /// </summary>
        public ClaimsPrincipal User
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpAuthenticationFeature>()?.User
                    ?? EmptyPrincipal;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(context);
                ArgumentNullException.ThrowIfNull(value);

                IHttpAuthenticationFeature? feature = context.Features.Get<IHttpAuthenticationFeature>();
                if (feature is null)
                {
                    context.Features.Set<IHttpAuthenticationFeature>(new HttpAuthenticationFeature(value));
                }
                else
                {
                    feature.User = value;
                }
            }
        }
    }
}
