
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

using Assimalign.Cohesion.Logging;
using Configuration;
using DependencyInjection;
using Hosting;

public abstract class ApplicationBuilder<TApplication> : IApplicationBuilder
    where TApplication : Application
{

    public virtual ConfigurationManager Configuration { get; }
    public virtual ServiceProviderBuilder Services { get; }

    public ILoggerFactoryBuilder Logging => throw new NotImplementedException();

    IHostBuilder IApplicationBuilder.Host => throw new NotImplementedException();
    IConfigurationManager IApplicationBuilder.Configuration => Configuration;
    IServiceProviderBuilder IApplicationBuilder.Services => Services;


    public abstract TApplication Build();

    IApplication IApplicationBuilder.Build()
    {
        throw new NotImplementedException();
    }
}
