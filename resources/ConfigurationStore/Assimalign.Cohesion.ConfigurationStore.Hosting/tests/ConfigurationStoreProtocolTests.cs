using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

using ConfigurationResourceCommand = Assimalign.Cohesion.ConfigurationStore.Client.ResourceCommand;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Client;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public sealed class ConfigurationStoreProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Protocol: real client lists, reads, sets, and removes values")]
    public async Task Client_WithLoopbackHost_ShouldRoundTripNamespaceOperations()
    {
        string dataPath = CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            string token = identity.Issue("configuration");
            ResourceContext context = ConfigurationStoreTestHost.CreateContext(
                endpoint,
                dataPath,
                token,
                identity.PublicKey);
            using IDisposable scope = ResourceRuntime.CreateScope(context);
            ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
            builder.AddNamespace("app", ns => ns
                .Set("Mode", "development")
                .Set("Optional", null));
            await using ConfigurationStoreApplication application = builder.Build();
            await ((IHost)application).StartAsync();

            try
            {
                IConfigurationStoreClient client = ConfigurationStoreClient.Create(
                    endpoint,
                    new ClientCredential(token));

                (await client.ListNamespacesAsync()).ShouldBe(["app"]);
                IReadOnlyDictionary<string, string?> initial = await client.GetNamespaceAsync("app");
                initial["Mode"].ShouldBe("development");
                initial["Optional"].ShouldBeNull();

                await client.SendCommandAsync(new ConfigurationResourceCommand(
                    "set-mode",
                    "configurationstore.set-value",
                    "appa",
                    "app/Mode",
                    Encoding.UTF8.GetBytes("{\"value\":\"production\"}")));
                (await client.GetNamespaceAsync("app"))["Mode"].ShouldBe("production");

                await client.SendCommandAsync(new ConfigurationResourceCommand(
                    "remove-optional",
                    "configurationstore.remove-value",
                    "appa",
                    "app/Optional",
                    ReadOnlyMemory<byte>.Empty));
                (await client.GetNamespaceAsync("app")).ContainsKey("Optional").ShouldBeFalse();
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

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Bootstrap authentication: missing token is 401 and wrong audience is 403")]
    public async Task Namespaces_WithMissingOrWrongAudienceCredential_ShouldEnforceAuthenticationStatus()
    {
        string dataPath = CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            string token = identity.Issue("configuration");
            ResourceContext context = ConfigurationStoreTestHost.CreateContext(
                endpoint,
                dataPath,
                token,
                identity.PublicKey);
            using IDisposable scope = ResourceRuntime.CreateScope(context);
            ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
            builder.AddNamespace("app", ns => ns.Set("Mode", "development"));
            await using ConfigurationStoreApplication application = builder.Build();
            await ((IHost)application).StartAsync();

            try
            {
                using var client = new HttpClient();
                Uri route = new(endpoint, "/cohesion/v1/namespaces?name=app");
                using HttpResponseMessage missing = await client.GetAsync(route);
                missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                missing.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");

                using var request = new HttpRequestMessage(HttpMethod.Get, route);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    identity.Issue("api"));
                using HttpResponseMessage wrongAudience = await client.SendAsync(request);
                wrongAudience.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

                IConfigurationStoreClient valid = ConfigurationStoreClient.Create(
                    endpoint,
                    new ClientCredential(token));
                (await valid.GetNamespaceAsync("app"))["Mode"].ShouldBe("development");
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
    public async Task Restart_WithChangedDeclaration_ShouldRetainCommandMutation()
    {
        string dataPath = CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            string token = identity.Issue("configuration");

            Uri firstEndpoint = ConfigurationStoreTestHost.GetEndpoint();
            ResourceContext firstContext = ConfigurationStoreTestHost.CreateContext(
                firstEndpoint,
                dataPath,
                token,
                identity.PublicKey);
            using (ResourceRuntime.CreateScope(firstContext))
            {
                ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
                builder.AddNamespace("app", ns => ns.Set("Mode", "declared-first"));
                await using ConfigurationStoreApplication application = builder.Build();
                await ((IHost)application).StartAsync();
                IConfigurationStoreClient client = ConfigurationStoreClient.Create(
                    firstEndpoint,
                    new ClientCredential(token));
                await client.SendCommandAsync(new ConfigurationResourceCommand(
                    "persist-mode",
                    "configurationstore.set-value",
                    "appa",
                    "app/Mode",
                    Encoding.UTF8.GetBytes("{\"value\":\"durable\"}")));
                await ((IHost)application).StopAsync();
            }

            Uri secondEndpoint = ConfigurationStoreTestHost.GetEndpoint();
            ResourceContext secondContext = ConfigurationStoreTestHost.CreateContext(
                secondEndpoint,
                dataPath,
                token,
                identity.PublicKey);
            using (ResourceRuntime.CreateScope(secondContext))
            {
                ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
                builder.AddNamespace("app", ns => ns.Set("Mode", "declared-second"));
                await using ConfigurationStoreApplication application = builder.Build();
                await ((IHost)application).StartAsync();
                try
                {
                    IConfigurationStoreClient client = ConfigurationStoreClient.Create(
                        secondEndpoint,
                        new ClientCredential(token));
                    (await client.GetNamespaceAsync("app"))["Mode"].ShouldBe("durable");
                    File.Exists(Path.Combine(dataPath, "trust", "trusted-issuers.json"))
                        .ShouldBeTrue();
                }
                finally
                {
                    await ((IHost)application).StopAsync();
                }
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Trust rotation: restart replaces the application issuer key")]
    public async Task Restart_WithRotatedApplicationTrustKey_ShouldAcceptOnlyTheCurrentKey()
    {
        string dataPath = CreateTemporaryDirectory();
        try
        {
            using var firstIdentity = new TestBootstrapIdentity();
            string firstToken = firstIdentity.Issue("configuration");
            Uri firstEndpoint = ConfigurationStoreTestHost.GetEndpoint();
            ResourceContext firstContext = ConfigurationStoreTestHost.CreateContext(
                firstEndpoint,
                dataPath,
                firstToken,
                firstIdentity.PublicKey);
            using (ResourceRuntime.CreateScope(firstContext))
            {
                ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
                builder.AddNamespace("app", ns => ns.Set("Mode", "durable"));
                await using ConfigurationStoreApplication application = builder.Build();
                await ((IHost)application).StartAsync();
                await ((IHost)application).StopAsync();
            }

            using var secondIdentity = new TestBootstrapIdentity();
            string secondToken = secondIdentity.Issue("configuration");
            Uri secondEndpoint = ConfigurationStoreTestHost.GetEndpoint();
            ResourceContext secondContext = ConfigurationStoreTestHost.CreateContext(
                secondEndpoint,
                dataPath,
                secondToken,
                secondIdentity.PublicKey);
            using (ResourceRuntime.CreateScope(secondContext))
            {
                ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
                await using ConfigurationStoreApplication application = builder.Build();
                await ((IHost)application).StartAsync();
                try
                {
                    IConfigurationStoreClient current = ConfigurationStoreClient.Create(
                        secondEndpoint,
                        new ClientCredential(secondToken));
                    (await current.GetNamespaceAsync("app"))["Mode"].ShouldBe("durable");

                    using var client = new HttpClient();
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        new Uri(secondEndpoint, "/cohesion/v1/namespaces?name=app"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", firstToken);
                    using HttpResponseMessage stale = await client.SendAsync(request);
                    stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                }
                finally
                {
                    await ((IHost)application).StopAsync();
                }
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Persistence failure: failed write leaves live values unchanged")]
    public async Task Command_WithUnwritableNamespaceDocument_ShouldRetainLiveValue()
    {
        string dataPath = CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            string token = identity.Issue("configuration");
            ResourceContext context = ConfigurationStoreTestHost.CreateContext(
                endpoint,
                dataPath,
                token,
                identity.PublicKey);
            using IDisposable scope = ResourceRuntime.CreateScope(context);
            ConfigurationStoreApplicationBuilder builder = ConfigurationStoreTestHost.CreateBuilder();
            builder.AddNamespace("app", ns => ns.Set("Mode", "original"));
            await using ConfigurationStoreApplication application = builder.Build();
            await ((IHost)application).StartAsync();

            try
            {
                string namespacePath = Directory.GetFiles(
                    Path.Combine(dataPath, "namespaces"),
                    "*.json",
                    SearchOption.TopDirectoryOnly).ShouldHaveSingleItem();
                File.Delete(namespacePath);
                Directory.CreateDirectory(namespacePath);

                IConfigurationStoreClient client = ConfigurationStoreClient.Create(
                    endpoint,
                    new ClientCredential(token));
                await Should.ThrowAsync<HttpRequestException>(() => client.SendCommandAsync(
                    new ConfigurationResourceCommand(
                        "failed-set",
                        "configurationstore.set-value",
                        "appa",
                        "app/Mode",
                        Encoding.UTF8.GetBytes("{\"value\":\"changed\"}"))));

                (await client.GetNamespaceAsync("app"))["Mode"].ShouldBe("original");
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
