
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Internal;

public static partial class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder) 
    {
        public IWebApplicationPipelineBuilder Use(Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
        {
            ArgumentNullException.ThrowIfNull(builder);
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
