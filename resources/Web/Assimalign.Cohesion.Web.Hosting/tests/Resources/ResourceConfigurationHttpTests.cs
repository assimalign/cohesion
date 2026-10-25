using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Management;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class ResourceConfigurationHttpTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [Web.Hosting] - Configuration: ";
    private const string CodeOrigin = "https://code.example";
    private const string DeployedOrigin = "https://deployed.example";
    private const string OverrideOrigin = "https://override.example";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-web-record-" + Guid.NewGuid().ToString("N"));

    public ResourceConfigurationHttpTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "The deployment record configures CORS before the first request and an override changes it live")]
    public async Task RunAsync_WithDeploymentRecord_AppliesItAndServesItsStatusAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Write(ResourceConfigurationRecord.DeploymentLayerFileName, """{"Web:Cors":{"defaultPolicy":{"origins":["https://deployed.example"]}}}""");
        Uri address = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using IDisposable scope = ResourceRuntime.CreateScope(CreateContext(address));
        CorsPolicyStore cors = CorsPolicyStore.Create(new CorsConfiguration { DefaultPolicy = new CorsPolicyConfiguration { Origins = [CodeOrigin] } });
        WebApplicationBuilder builder = WebApplication.CreateBuilder([], typeof(ResourceConfigurationHttpTests).Assembly);
        builder.Services.AddSingleton<ResourceConfigurationSection>(cors.AsResourceConfiguration());
        await using WebApplication application = builder.Build();
        application.UseDynamicCors(cors);
        application.Run(context =>
        {
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.NoContent;
            return Task.CompletedTask;
        });
        Task run = application.RunAsync(timeout.Token);
        try
        {
            using var client = new HttpClient { BaseAddress = address };
            await WaitUntilReadyAsync(client, timeout.Token);

            // Act
            string? deployed = await ReadAllowedOriginAsync(client, DeployedOrigin, timeout.Token);
            string? code = await ReadAllowedOriginAsync(client, CodeOrigin, timeout.Token);
            using JsonDocument startup = await GetJsonAsync(client, "/cohesion/v1/configuration/status", timeout.Token);
            Write(ResourceConfigurationRecord.OverrideLayerFileName, """{"Web:Cors":{"defaultPolicy":{"origins":["https://override.example"]}}}""");
            ResourceConfigurationRecord changed = ResourceConfigurationRecord.Load(_directory);
            await builder.ControlPlane!.ApplyConfigurationAsync(changed, ResourceConfigurationPhase.Running, timeout.Token);
            string? overridden = await ReadAllowedOriginAsync(client, OverrideOrigin, timeout.Token);

            // Assert
            deployed.ShouldBe(DeployedOrigin);
            code.ShouldBeNull();
            JsonElement configuration = startup.RootElement.GetProperty("configuration");
            configuration.GetProperty("sections")[0].GetProperty("path").GetString().ShouldBe("Web:Cors");
            configuration.GetProperty("sections")[0].GetProperty("state").GetString().ShouldBe("applied");
            startup.RootElement.GetProperty("surfaceDigest").GetString().ShouldBe(builder.ControlPlane.Surface.Digest);
            startup.RootElement.GetProperty("instance").GetString().ShouldBe("web-0");
            overridden.ShouldBe(OverrideOrigin);
            using JsonDocument running = await GetJsonAsync(client, "/cohesion/v1/configuration/status", timeout.Token);
            running.RootElement.GetProperty("configuration").GetProperty("recordDigest").GetString().ShouldBe(changed.Digest);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
            await run.WaitAsync(timeout.Token);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "The surface route serves the frozen runtime surface")]
    public async Task ConfigurationRoute_WithRegisteredSection_ServesTheSurfaceAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Uri address = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(endpoints: new Dictionary<string, Uri> { ["http"] = address }));
        CorsPolicyStore cors = CorsPolicyStore.Create(new CorsConfiguration());
        WebApplicationBuilder builder = WebApplication.CreateBuilder([], typeof(ResourceConfigurationHttpTests).Assembly);
        builder.Services.AddSingleton<ResourceConfigurationSection>(cors.AsResourceConfiguration());
        await using WebApplication application = builder.Build();
        await ((IHost)application).StartAsync(timeout.Token);
        try
        {
            using var client = new HttpClient { BaseAddress = address };

            // Act
            using JsonDocument surface = await GetJsonAsync(client, "/cohesion/v1/configuration", timeout.Token);
            using HttpResponseMessage post = await client.PostAsync("/cohesion/v1/configuration", content: null, timeout.Token);

            // Assert
            surface.RootElement.GetProperty("schema").GetString().ShouldBe(ResourceRuntimeSurface.DocumentSchema);
            JsonElement section = surface.RootElement.GetProperty("configuration")[0];
            section.GetProperty("path").GetString().ShouldBe("Web:Cors");
            section.GetProperty("mutability").GetString().ShouldBe("live");
            post.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "The actions route binds input, runs the action and refuses unknown names")]
    public async Task ActionsRoute_WithRegisteredAction_InvokesItAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Uri address = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(endpoints: new Dictionary<string, Uri> { ["http"] = address }));
        WebApplicationBuilder builder = WebApplication.CreateBuilder([], typeof(ResourceConfigurationHttpTests).Assembly);
        builder.Services.AddSingleton<ResourceAction>(ResourceAction.Create(
            "echo",
            EchoActionContext.Default.EchoInput,
            EchoActionContext.Default.EchoResult,
            ResourceActionTarget.AnyInstance,
            (input, cancellationToken) => ValueTask.FromResult(new EchoResult(input.Text, input.Text.Length))));
        await using WebApplication application = builder.Build();
        await ((IHost)application).StartAsync(timeout.Token);
        try
        {
            using var client = new HttpClient { BaseAddress = address };

            // Act
            using HttpResponseMessage echoed = await PostJsonAsync(client, "/cohesion/v1/actions/echo", """{"text":"ping"}""", timeout.Token);
            using HttpResponseMessage invalid = await PostJsonAsync(client, "/cohesion/v1/actions/echo", """{"text":5}""", timeout.Token);
            using HttpResponseMessage unknown = await PostJsonAsync(client, "/cohesion/v1/actions/flush", "{}", timeout.Token);
            using HttpResponseMessage malformed = await PostJsonAsync(client, "/cohesion/v1/actions/Not_A_Name", "{}", timeout.Token);
            using HttpResponseMessage read = await client.GetAsync("/cohesion/v1/actions/echo", timeout.Token);

            // Assert
            echoed.StatusCode.ShouldBe(HttpStatusCode.OK);
            using (JsonDocument result = JsonDocument.Parse(await echoed.Content.ReadAsStringAsync(timeout.Token)))
            {
                result.RootElement.GetProperty("text").GetString().ShouldBe("ping");
                result.RootElement.GetProperty("length").GetInt32().ShouldBe(4);
            }

            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            malformed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            read.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    private ResourceContext CreateContext(Uri address) => new(
        applicationName: null,
        resourceName: null,
        environmentName: null,
        gatewayName: null,
        contentRootPath: null,
        endpoints: new Dictionary<string, Uri> { ["http"] = address },
        mounts: null,
        settings: null,
        references: null,
        bootstrapCredential: default,
        applicationTrustKey: default,
        ambientValues: new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.ConfigDirectory] = _directory,
            [AppEnvironment.Variables.ResourceInstance] = "web-0",
        });

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_directory, name), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json, CancellationToken cancellationToken) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    private static async Task<string?> ReadAllowedOriginAsync(HttpClient client, string origin, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/data");
        request.Headers.Add("Origin", origin);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values)
            ? string.Join(",", values)
            : null;
    }

    private static async Task WaitUntilReadyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync("/cohesion/v1/readyz", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener has not completed binding yet.
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
