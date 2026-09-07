using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ConfigurationStore;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationStoreApplicationBuilder : IConfigurationStoreApplicationBuilder
{
    internal ConfigurationStoreApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IConfigurationStoreApplication Build()
    {
        var options = new ConfigurationStoreApplicationOptions();
        var context = new ConfigurationStoreApplicationContext();

        return new ConfigurationStoreApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
