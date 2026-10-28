using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ConfigurationResourceCommand = Assimalign.Cohesion.ConfigurationStore.Client.ResourceCommand;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Client;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
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

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Bootstrap authentication: missing token is 401 and wrong audience or telemetry scope is 403")]
    public async Task Namespaces_WithMissingWrongAudienceOrTelemetryCredential_ShouldEnforceAuthenticationStatus()
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

                using var telemetryRequest = new HttpRequestMessage(HttpMethod.Get, route);
                telemetryRequest.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    identity.Issue("configuration", telemetry: true));
                using HttpResponseMessage telemetry = await client.SendAsync(telemetryRequest);
                telemetry.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

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

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Health: an exception a contributor places in its data is absent from the unauthenticated probes")]
    public async Task HealthProbes_WhenContributorDataHoldsException_ShouldNotPublishIt()
    {
        // Arrange
        const string hostName = "db-primary.corp.internal";
        string dataPath = CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            ResourceContext context = ConfigurationStoreTestHost.CreateContext(
                endpoint,
                dataPath,
                identity.Issue("configuration"),
                identity.PublicKey);
            using IDisposable scope = ResourceRuntime.CreateScope(context);
            await using ConfigurationStoreApplication application = ConfigurationStoreTestHost.CreateBuilder().Build();
            ResourceRuntime.TryGetControlPlane((IHost)application, out IResourceControlPlane? plane).ShouldBeTrue();
            plane.ShouldNotBeNull().AddHealthContributor(new DataContributor(
                "database",
                HealthContribution.Unhealthy(
                    "database unreachable",
                    new Dictionary<string, object> { ["error"] = new InvalidOperationException(hostName) })));
            await ((IHost)application).StartAsync();

            try
            {
                using var client = new HttpClient();
                foreach (string path in new[] { "/healthz", "/readyz", "/livez" })
                {
                    // Act
                    using HttpResponseMessage response = await client.GetAsync(new Uri(endpoint, path));
                    string body = await response.Content.ReadAsStringAsync();

                    // Assert
                    response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, path);
                    body.ShouldNotContain(hostName, Case.Insensitive, path);
                    body.ShouldNotContain(nameof(InvalidOperationException), Case.Sensitive, path);
                    using JsonDocument document = JsonDocument.Parse(body);
                    document.RootElement.GetProperty("contributions").GetProperty("database")
                        .GetProperty("data").GetProperty("error").GetString()
                        .ShouldBe("The contributor reported an exception.", path);
                }
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

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Health: an exception a custom control plane reports in data is absent from the unauthenticated probes")]
    public async Task HealthProbes_WhenCustomControlPlaneReportsExceptionInData_ShouldNotPublishIt()
    {
        // Arrange — a custom IResourceControlPlane stands in for the default one and passes an exception in
        // a contribution's data through unfiltered, so the endpoint's writer is the only thing between the
        // exception and the probe.
        const string message = "Failed to connect to db-primary.corp.internal:5432.";
        string dataPath = CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            ResourceContext context = ConfigurationStoreTestHost.CreateContext(
                endpoint,
                dataPath,
                identity.Issue("configuration"),
                identity.PublicKey);
            using IDisposable scope = ResourceRuntime.CreateScope(context);
            ConfigurationStoreTestHost.DecorateControlPlane(inner => new ExceptionDataControlPlane(
                inner,
                "database",
                HealthContribution.Unhealthy(
                    "database unreachable",
                    new Dictionary<string, object> { ["error"] = new InvalidOperationException(message) })));
            await using ConfigurationStoreApplication application = ConfigurationStoreTestHost.CreateBuilder().Build();
            await ((IHost)application).StartAsync();

            try
            {
                using var client = new HttpClient();
                foreach (string path in new[] { "/healthz", "/readyz", "/livez" })
                {
                    // Act
                    using HttpResponseMessage response = await client.GetAsync(new Uri(endpoint, path));
                    string body = await response.Content.ReadAsStringAsync();

                    // Assert
                    response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, path);
                    body.ShouldNotContain(message, Case.Insensitive, path);
                    body.ShouldNotContain(nameof(InvalidOperationException), Case.Sensitive, path);
                    using JsonDocument document = JsonDocument.Parse(body);
                    document.RootElement.GetProperty("contributions").GetProperty("database")
                        .GetProperty("data").GetProperty("error").GetString()
                        .ShouldBe("The contributor reported an exception.", path);
                }
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

    private sealed class DataContributor : IHealthContributor
    {
        private readonly HealthContribution _contribution;

        internal DataContributor(string name, HealthContribution contribution)
        {
            Name = name;
            _contribution = contribution;
        }

        public string Name { get; }

        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_contribution);
    }

    /// <summary>
    /// A custom control plane over the default one that adds one contribution to every health, readiness
    /// and liveness report as it is, without the default control plane's filtering.
    /// </summary>
    private sealed class ExceptionDataControlPlane : IResourceControlPlane
    {
        private readonly IResourceControlPlane _inner;
        private readonly string _name;
        private readonly HealthContribution _contribution;

        internal ExceptionDataControlPlane(IResourceControlPlane inner, string name, HealthContribution contribution)
        {
            _inner = inner;
            _name = name;
            _contribution = contribution;
        }

        public IReadOnlyList<string> AcceptedCommandKinds => _inner.AcceptedCommandKinds;

        public IReadOnlyList<Assimalign.Cohesion.Hosting.Resources.ResourceCommand> Commands => _inner.Commands;

        public IReadOnlyDictionary<string, Uri> ObservedEndpoints => _inner.ObservedEndpoints;

        public void RegisterCommandHandler(IResourceCommandHandler handler) => _inner.RegisterCommandHandler(handler);

        public void AddHealthContributor(IHealthContributor contributor) => _inner.AddHealthContributor(contributor);

        public void ObserveEndpoint(string name, Uri address) => _inner.ObserveEndpoint(name, address);

        public async ValueTask<ResourceHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            AddContribution(await _inner.CheckHealthAsync(cancellationToken));

        public async ValueTask<ResourceHealthReport> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
            AddContribution(await _inner.CheckReadinessAsync(cancellationToken));

        public async ValueTask<ResourceHealthReport> CheckLivenessAsync(CancellationToken cancellationToken = default) =>
            AddContribution(await _inner.CheckLivenessAsync(cancellationToken));

        public void AttachHost(IHost host) => _inner.AttachHost(host);

        public ValueTask RequestStopAsync(CancellationToken cancellationToken = default) => _inner.RequestStopAsync(cancellationToken);

        public ValueTask<ReadOnlyMemory<byte>> ExecuteCommandAsync(
            Assimalign.Cohesion.Hosting.Resources.ResourceCommand command,
            CancellationToken cancellationToken = default) => _inner.ExecuteCommandAsync(command, cancellationToken);

        public ValueTask<ReadOnlyMemory<byte>> DeleteCommandAsync(
            Assimalign.Cohesion.Hosting.Resources.ResourceCommand command,
            CancellationToken cancellationToken = default) => _inner.DeleteCommandAsync(command, cancellationToken);

        private ResourceHealthReport AddContribution(ResourceHealthReport report)
        {
            var contributions = new Dictionary<string, HealthContribution>(report.Contributions, StringComparer.Ordinal)
            {
                [_name] = _contribution,
            };

            return new ResourceHealthReport(HealthStatus.Unhealthy, contributions);
        }
    }
}
