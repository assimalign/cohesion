
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Internal;

public static class WebApplicationPipelineBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder
    {
        public TBuilder Use(Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);

            Func<IHttpContext, WebApplicationMiddleware, Task> middleware2 = middleware;

            builder.Use((WebApplicationMiddleware next) => (IHttpContext context) =>
            {
                return middleware2.Invoke(context, next);
            });

            return builder;
        }
    }
}
