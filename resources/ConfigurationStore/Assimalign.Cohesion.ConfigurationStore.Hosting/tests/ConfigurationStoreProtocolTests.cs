using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Client;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public sealed class ConfigurationStoreProtocolTests
{
    // The plain host takes no credential; the client presents one on every request regardless.
    private const string UnusedToken = "plain-host";

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Protocol: real client lists and reads values")]
    public async Task Client_WithLoopbackHost_ShouldListAndReadNamespaces()
    {
        string dataPath = CreateTemporaryDirectory();
        try
        {
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder(endpoint, dataPath);
            builder.AddNamespace("app", ns => ns
                .Set("Mode", "development")
                .Set("Optional", null));
            await using ConfigurationStoreApplication application = builder.Build();
            await ((IHost)application).StartAsync();

            try
            {
                IConfigurationStoreClient client = ConfigurationStoreClient.Create(
                    endpoint,
                    new ClientCredential(UnusedToken));

                (await client.ListNamespacesAsync()).ShouldBe(["app"]);
                IReadOnlyDictionary<string, string?> initial = await client.GetNamespaceAsync("app");
                initial["Mode"].ShouldBe("development");
                initial["Optional"].ShouldBeNull();
            }
            finally
            {
                await ((IHost)application).StopAsync();
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Persistence: durable values override declarations after restart")]
    public async Task Restart_WithChangedDeclaration_ShouldRetainDurableValue()
    {
        string dataPath = CreateTemporaryDirectory();
        try
        {
            Uri firstEndpoint = ConfigurationStoreTestHost.GetEndpoint();
            ConfigurationStoreApplicationBuilder firstBuilder = ConfigurationStoreTestHost.CreateBuilder(firstEndpoint, dataPath);
            firstBuilder.AddNamespace("app", ns => ns.Set("Mode", "declared-first"));
            await using (ConfigurationStoreApplication application = firstBuilder.Build())
            {
                await ((IHost)application).StartAsync();
                await ((IHost)application).StopAsync();
            }

            Uri secondEndpoint = ConfigurationStoreTestHost.GetEndpoint();
            ConfigurationStoreApplicationBuilder secondBuilder = ConfigurationStoreTestHost.CreateBuilder(secondEndpoint, dataPath);
            secondBuilder.AddNamespace("app", ns => ns.Set("Mode", "declared-second"));
            await using ConfigurationStoreApplication restarted = secondBuilder.Build();
            await ((IHost)restarted).StartAsync();
            try
            {
                IConfigurationStoreClient client = ConfigurationStoreClient.Create(
                    secondEndpoint,
                    new ClientCredential(UnusedToken));
                (await client.GetNamespaceAsync("app"))["Mode"].ShouldBe("declared-first");
            }
            finally
            {
                await ((IHost)restarted).StopAsync();
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "cohesion-configuration-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
