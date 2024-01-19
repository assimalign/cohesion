using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Net.Http;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Hosting;

public static class OGraphHostBuilderExtensions
{
    public static IHostBuilder AddOGraph(this IHostBuilder builder)
    {
        return builder.AddHttpServer(server =>
        {
            server.ConfigureServer(options =>
            {
                //options.UseExecutor()
            });
        });
    }
}
