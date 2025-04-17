
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

using Configuration;
using DependencyInjection;
using Hosting;

public abstract class ApplicationBuilder<TApplication> : IApplicationBuilder
    where TApplication : Application
{
    public ApplicationBuilder(HostBuilder host)
    {
        Host = host;
    }


    public virtual HostBuilder Host { get; }
    public virtual ConfigurationManager Configuration { get; }
    public virtual ServiceProviderBuilder Services { get; }

    IHostBuilder IApplicationBuilder.Host => Host;
    IConfigurationManager IApplicationBuilder.Configuration => Configuration;
    IServiceProviderBuilder IApplicationBuilder.Services => Services;


    public abstract TApplication Build();

    IApplication IApplicationBuilder.Build()
    {
        throw new NotImplementedException();
    }
}
