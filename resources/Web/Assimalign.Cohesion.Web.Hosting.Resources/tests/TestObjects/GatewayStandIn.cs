using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

/// <summary>
/// A throwaway stand-in for the application-model gateway: it receives the manifests instances
/// publish on <c>POST /manifests</c> and answers with the snapshots a test scripts.
/// </summary>
internal sealed class GatewayStandIn : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<ResourceManifest> _manifests = new();
    private byte[] _answer = "[]"u8.ToArray();
    private int _refusal;

    private GatewayStandIn(WebApplication application, Uri manifestEndpoint)
    {
        _application = application;
        ManifestEndpoint = manifestEndpoint;
    }

    /// <summary>
    /// Gets the URL instances publish their manifests to.
    /// </summary>
    public Uri ManifestEndpoint { get; }

    /// <summary>
    /// Gets the manifests received so far, in arrival order.
    /// </summary>
    public IReadOnlyCollection<ResourceManifest> Manifests => _manifests;

    public static async Task<GatewayStandIn> StartAsync()
    {
        int port = TestPorts.GetFree();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port)));

        WebApplication application = builder.Build();
        var gateway = new GatewayStandIn(application, new Uri($"http://127.0.0.1:{port}/manifests"));
        application.Use((context, _) => gateway.ReceiveAsync(context));

        await ((IWebApplication)application).StartAsync();
        return gateway;
    }

    /// <summary>
    /// Answers later publications with these snapshots, as the JSON array the publisher reads.
    /// </summary>
    public void AnswerWith(string snapshots)
    {
        Volatile.Write(ref _answer, Encoding.UTF8.GetBytes(snapshots));
    }

    /// <summary>
    /// Refuses later publications with this status.
    /// </summary>
    public void RefuseWith(int status)
    {
        Volatile.Write(ref _refusal, status);
    }

    public async ValueTask DisposeAsync()
    {
        await ((IWebApplication)_application).StopAsync();
        await ((IAsyncDisposable)_application).DisposeAsync();
    }

    private async Task ReceiveAsync(IHttpContext context)
    {
        if (context.Request.Method != HttpMethod.Post || context.Request.Path.Value != "/manifests")
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestCancelled);
        _manifests.Enqueue(JsonSerializer.Deserialize(body.ToArray(), ResourceJsonContext.Default.ResourceManifest)!);

        if (Volatile.Read(ref _refusal) is int refusal and > 0)
        {
            context.Response.StatusCode = refusal;
            return;
        }

        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        await context.Response.Body.WriteAsync(Volatile.Read(ref _answer), context.RequestCancelled);
    }
}
