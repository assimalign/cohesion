using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Net.Http;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.OGraph;
using Assimalign.Cohesion.DependencyInjection;

namespace Assimalign.Cohesion.Hosting;

public static class OGraphHostBuilderExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static OGraphApplicationBuilder AddOGraphServer(this IHostBuilder builder)
    {
        var app = new OGraphApplicationBuilder();

        builder.AddHttpServer(server =>
        {
            var serviceProvider = app.Services.Build();
            var configurationBuilder = app.Configuration;

            server.ConfigureServer(options =>
            {
                options.UseExecutor(new OGraphExecutor());
            });

            var configuration = configurationBuilder.Build();

            server.ConfigureServiceProvider(serviceProvider);
        });

        return app;
    }

}
