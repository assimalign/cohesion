using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;

public static class WebApplicationExtensions
{
    extension(IWebApplicationBuilder builder)
    {
        public IWebApplicationBuilder AddHttpFormFeature()
        {


            return builder;
        }
    }


    extension(IWebApplicationPipelineBuilder builder)
    {
        public IWebApplicationPipelineBuilder UseHttpForms()
        {


            builder.Use(async (context, next) =>
            {
                IHttpFormFeature feature = context.Features.Get<IHttpFormFeature>();
            });

            return builder;
        }
    }
}
