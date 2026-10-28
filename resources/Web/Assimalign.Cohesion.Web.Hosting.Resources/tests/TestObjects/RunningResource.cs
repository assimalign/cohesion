using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>
/// A Web application started as the <c>orders-api</c> resource instance, the way a provisioned
/// resource's <c>Program.cs</c> composes it, on its own port and with a private control-plane port.
/// Its CORS policy is registered with <c>AddCors</c> and placed with <c>UseCors()</c>, as any Web
/// application's is; <c>AddResource</c> adds nothing to that composition.
/// </summary>
internal sealed class RunningResource : IAsyncDisposable
{
    private readonly WebApplication _application;

    private RunningResource(WebApplication application, int applicationPort, int controlPlanePort)
    {
        _application = application;
        Application = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{applicationPort}/") };
        ControlPlane = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{controlPlanePort}/") };
    }

    /// <summary>
    /// Gets the resource instance the managed application's control plane serves.
    /// </summary>
    public IResource Resource => ResourceServices.Single().ControlPlane.Resource;

    /// <summary>
    /// Gets the default CORS policy in effect, as the <c>cors</c> action reports it.
    /// </summary>
    public WebCorsPolicy DefaultCorsPolicy => WebCorsPolicyMapping.FromPolicy(
        _application.Context.Settings.OfType<IWebApplicationSetting<CorsPolicy?>>().Single().Value);

    /// <summary>
    /// Gets a client for the application's own listener.
    /// </summary>
    public HttpClient Application { get; }

    /// <summary>
    /// Gets a client for the instance's control plane, as the gateway calls it.
    /// </summary>
    public HttpClient ControlPlane { get; }

    /// <summary>
    /// Gets the resource services among the host's services, as an in-process gateway finds them.
    /// </summary>
    public IReadOnlyList<IResourceService> ResourceServices => _application.Context.ServiceProvider
        .GetRequiredService<IEnumerable<IHostService>>()
        .OfType<IResourceService>()
        .ToArray();

    /// <param name="manifestEndpoint">Where the instance publishes; <see langword="null"/> runs it unmanaged.</param>
    /// <param name="cors">The CORS policy the application's code defines; <see langword="null"/> registers no CORS at all.</param>
    /// <param name="configure">Configures the resource further.</param>
    /// <param name="beforeResource">Composes the application ahead of <c>AddResource</c>.</param>
    public static async Task<RunningResource> StartAsync(
        Uri? manifestEndpoint,
        WebCorsPolicy? cors,
        Action<WebResourceOptions>? configure = null,
        Action<WebApplicationBuilder>? beforeResource = null)
    {
        int applicationPort = TestPorts.GetFree();
        int controlPlanePort = TestPorts.GetFree();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, applicationPort)));
        if (cors is not null)
        {
            builder.Services.AddCors(registration =>
            {
                if (WebCorsPolicyMapping.ToPolicy(cors) is { } policy)
                {
                    registration.Options.AddDefaultPolicy(policy);
                }
            });
        }
        beforeResource?.Invoke(builder);
        builder.AddResource("orders-api", resource =>
        {
            resource.ManifestEndpoint = manifestEndpoint;
            resource.ControlPlaneEndpoint = new Uri($"http://127.0.0.1:{controlPlanePort}/");
            configure?.Invoke(resource);
        });

        WebApplication application = builder.Build();
        application.UseCors();
        application.Use((context, _) =>
        {
            context.Response.StatusCode = HttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        try
        {
            await ((IWebApplication)application).StartAsync();
        }
        catch
        {
            await ((IAsyncDisposable)application).DisposeAsync();
            throw;
        }

        return new RunningResource(application, applicationPort, controlPlanePort);
    }

    /// <summary>
    /// Sends a cross-origin request from an origin and returns the origin the application allowed, or
    /// <see langword="null"/> when it allowed none.
    /// </summary>
    public async Task<string?> GetAllowedOriginAsync(string origin)
    {
        using var request = new HttpRequestMessage(NetHttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using HttpResponseMessage response = await Application.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out IEnumerable<string>? values)
            ? values.Single()
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        Application.Dispose();
        ControlPlane.Dispose();
        await ((IWebApplication)_application).StopAsync();
        await ((IAsyncDisposable)_application).DisposeAsync();
    }
}
