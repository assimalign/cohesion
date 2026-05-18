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
            return builder.Use(async (context, next) =>
            {
                // Get the feature
                IHttpFeatureCollection features = context.Features;
                IHttpRequestCookieFeature? feature = features.Get<IHttpRequestCookieFeature>();

                if (feature is null)
                {
                    features.Set<IHttpRequestCookieFeature>(new HttpRequestCookieFeature());
                }
            });
        }
    }
}
