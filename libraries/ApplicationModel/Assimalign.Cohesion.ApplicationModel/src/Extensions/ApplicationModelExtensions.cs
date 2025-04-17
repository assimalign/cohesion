using Assimalign.Cohesion.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

public static class ApplicationModelExtensions
{

    public static IHostBuilder ConfigureApplication<TApp>(
        this IHostBuilder builder,
        Func<ApplicationBuilder<TApp>, TApp> configure)
        where TApp : Application
    {
        builder.AddService(context =>
        {



        });

        return builder;
    }
}
