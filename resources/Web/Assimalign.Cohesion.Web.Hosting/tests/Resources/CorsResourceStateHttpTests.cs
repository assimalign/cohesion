using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Management;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class CorsResourceStateHttpTests
{
    static CorsResourceStateHttpTests() => ResourceRuntime.RegisterControlPlane(typeof(CorsResourceStateHandler).Assembly, () =>
    {
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "http", Path = "/management/v1" });
        return plane;
    });

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - State: CORS revision changes real requests and rejects stale processes")]
    public async Task StateHttp_WithCorsUpdateRetryAndInvalidValue_ShouldChangeRealPipelineAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Uri address = Uri.CreateEndpoint("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(endpoints: new Dictionary<string, Uri> { ["http"] = address }));
        CorsPolicyStore cors = CorsPolicyStore.Create(new CorsConfiguration { DefaultPolicy = new CorsPolicyConfiguration { Origins = ["https://old.example"] } });
        WebApplicationBuilder builder = WebApplication.CreateBuilder([], typeof(CorsResourceStateHandler).Assembly);
        builder.Services.AddSingleton<IResourceStateHandler>(CorsResourceStateHandler.Create(cors));
        await using WebApplication application = builder.Build();
        application.UseDynamicCors(cors);
        application.Run(context => { context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.NoContent; return Task.CompletedTask; });
        await ((IHost)application).StartAsync(timeout.Token);
        try
        {
            using var client = new HttpClient { BaseAddress = address };
            using HttpResponseMessage catalog = await client.GetAsync("/management/v1/runtime", timeout.Token);
            catalog.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument description = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync(timeout.Token));
            Guid process = description.RootElement.GetProperty("processStartId").GetGuid();
            description.RootElement.GetProperty("manifest").GetProperty("states")[0].GetProperty("name").GetString().ShouldBe("web.cors");
            (await ReadOriginAsync(client, "https://old.example", timeout.Token)).ShouldBe("https://old.example");
            Guid incarnation = Guid.NewGuid();
            const string desired = "{\"defaultPolicy\":{\"origins\":[\"https://new.example\"]}}";
            using HttpResponseMessage wrongProcess = await ApplyAsync(client, Guid.NewGuid(), incarnation, 1, desired, timeout.Token);
            wrongProcess.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            using HttpResponseMessage update = await ApplyAsync(client, process, incarnation, 1, desired, timeout.Token);
            update.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ReadOriginAsync(client, "https://new.example", timeout.Token)).ShouldBe("https://new.example");
            (await ReadOriginAsync(client, "https://old.example", timeout.Token)).ShouldBeNull();
            using HttpResponseMessage retry = await ApplyAsync(client, process, incarnation, 1, desired, timeout.Token);
            retry.StatusCode.ShouldBe(HttpStatusCode.OK);
            using HttpResponseMessage invalid = await ApplyAsync(client, process, incarnation, 2,
                "{\"defaultPolicy\":{\"allowAnyOrigin\":true,\"allowCredentials\":true}}", timeout.Token);
            invalid.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            cors.ReadState().Revision.ShouldBe(1);
            (await ReadOriginAsync(client, "https://new.example", timeout.Token)).ShouldBe("https://new.example");
        }
        finally { await ((IHost)application).StopAsync(CancellationToken.None); }
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, Guid process, Guid incarnation, long revision, string desired, CancellationToken cancellationToken)
    {
        // These test payloads are already in the protocol's sorted-property order.
        string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("{\"exists\":true,\"value\":" + desired + "}")));
        string context = JsonSerializer.Serialize(new ResourceStateApplyContext(incarnation, process, revision, digest, revision), ResourceStateJsonContext.Default.ResourceStateApplyContext);
        var content = new StringContent("{\"context\":" + context + ",\"desired\":{\"exists\":true,\"value\":" + desired + "}}", Encoding.UTF8, "application/json");
        return client.PutAsync("/management/v1/state/web.cors/configuration", content, cancellationToken);
    }

    private static async Task<string?> ReadOriginAsync(HttpClient client, string origin, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/data");
        request.Headers.Add("Origin", origin);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values) ? string.Join(",", values) : null;
    }

}
