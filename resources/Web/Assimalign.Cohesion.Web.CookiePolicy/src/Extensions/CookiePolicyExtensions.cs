using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.CookiePolicy;

public static class CookiePolicyExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        public IWebApplicationPipelineBuilder UseCookiePolicy(Action<CookiePolicyOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return builder.Use(async (context, next) =>
            {
                // Cookies are surfaced by Assimalign.Cohesion.Http.Cookies through the
                // request/response cookie extensions, so no manual feature install is
                // required here. Cookie-policy enforcement is part of the web
                // application-model work and is not yet applied.
                await next.Invoke(context).ConfigureAwait(false);
            });
        }
    }
}
