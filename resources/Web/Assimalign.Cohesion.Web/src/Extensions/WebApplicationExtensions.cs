
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Internal;

public static class WebApplicationExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder
    {
        public TBuilder Use(Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
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


    //extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    //{
    //    /// <summary>
    //    /// 
    //    /// </summary>
    //    /// <param name="middleware"></param>
    //    /// <returns></returns>
    //    /// <exception cref="InvalidOperationException"></exception>
    //    public TBuilder Use(Func<IServiceProvider?, IHttpContext, WebApplicationMiddleware, Task> middleware)
    //    {
    //        ArgumentNullException.ThrowIfNull(builder);
    //        ArgumentNullException.ThrowIfNull(middleware);

    //        if (builder.Context.ServiceProvider is null)
    //        {
    //            throw new InvalidOperationException("No IServiceProvider was registered.");
    //        }

    //        IServiceProvider serviceProvider = builder.Context.ServiceProvider;
    //        Func<IServiceProvider, IHttpContext, WebApplicationMiddleware, Task> middleware1 = middleware;

    //        builder.Use((WebApplicationMiddleware next) => (IHttpContext httpContext) =>
    //        {
    //            return middleware1.Invoke(serviceProvider, httpContext, next);
    //        });
    //        return builder;
    //    }
    //}
}
