using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Hosting;
using Configuration;
using DependencyInjection;
using Assimalign.Cohesion.Logging;

public sealed class WebApplicationBuilder
{
    public WebApplicationBuilder()
    {
        var builder = HostBuilder.Create(options =>
        {
            options.OnTrace(context =>
            {
                if (context.ServiceProvider is null)
                {
                    return;
                }

                var loggerFactory = context.ServiceProvider.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.Create("");

                context.
            });
        });

        builder.AddService()
    }

    public IConfiguration Configuration { get; }



}
