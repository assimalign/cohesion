using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.SecretStore;

namespace Assimalign.Cohesion.SecretStore.Hosting;

internal sealed class SecretStoreApplicationBuilder : ISecretStoreApplicationBuilder
{
    internal SecretStoreApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public ISecretStoreApplication Build()
    {
        var options = new SecretStoreApplicationOptions();
        var context = new SecretStoreApplicationContext();

        return new SecretStoreApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
