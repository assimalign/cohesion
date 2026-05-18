using System;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http;

public static class HttpContextIdentityExtensions
{
    extension(IHttpRequest request)
    {
        public ClaimsPrincipal User
        {
            get
            {
                ArgumentNullException.ThrowIfNull(request);

                IHttpIdentityFeature? feature = request.HttpContext.Features.Get<IHttpIdentityFeature>();

                // If no feature was added then we return an empty collection.
                if (feature is null || feature.ClaimsPrincipal is null)
                {
                    return ClaimsPrincipal.Current;
                }
                return feature.ClaimsPrincipal;
            }
        }
    }
}
