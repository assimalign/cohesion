using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

public static class WebApplicationPipelineBuilderExtensions
{


    public static IWebApplicationPipelineBuilder MapGet(
        this IWebApplicationPipelineBuilder builder, 
        Route route, 
        Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
    {



        return builder;
    }
}
