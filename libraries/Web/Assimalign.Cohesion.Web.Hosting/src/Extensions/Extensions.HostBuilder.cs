using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Hosting;

public static class HostBuilderExtensions
{

    public static IHostBuilder AddWebServer(this IHostBuilder builder, Action<WebApplicationBuilder> configure)
    {


        return builder;
    }
}
