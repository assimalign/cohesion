using Assimalign.Cohesion.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

public static partial class WebApplicationBuilderExtensions
{

    public static WebApplicationBuilder AddAuthentication(this WebApplicationBuilder builder)
    {
        


        return builder;
    }

    public static WebApplicationBuilder AddAuthorization(this WebApplicationBuilder builder)
    {



        return builder;
    }



    public static IHostBuilder ConfigureWebApplication(this IHostBuilder builder, Func<WebApplicationBuilder>)
}
