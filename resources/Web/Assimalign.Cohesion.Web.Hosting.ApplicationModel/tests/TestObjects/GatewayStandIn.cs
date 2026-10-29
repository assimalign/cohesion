using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Http;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// A throwaway stand-in for the application-model gateway: it receives the manifests instances
/// publish on <c>POST /manifests</c>, answers with the snapshots and lease a test scripts, and
/// counts the renewals instances send on <c>PUT /manifests</c>.
/// </summary>
internal sealed class GatewayStandIn : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<ResourceManifest> _manifests = new();
    private readonly JsonResourceContentSerializer _json = JsonResourceContentSerializer.Create();
    private byte[] _answer = "[]"u8.ToArray();
    private string _lease = "null";
    private int _refusal;
    private int _renewals;
    private volatile bool _forgetful;

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

    /// <summary>
    /// Gets how many renewals instances have sent.
    /// </summary>
    public int Renewals => Volatile.Read(ref _renewals);

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
    /// Answers later publications with these snapshots, given as a JSON array.
    /// </summary>
    public void AnswerWith(string snapshots)
    {
        Volatile.Write(ref _answer, Encoding.UTF8.GetBytes(snapshots));
    }

    /// <summary>
    /// Grants later publications a lease, so their instances renew.
    /// </summary>
    public void LeaseWith(TimeSpan lease)
    {
        Volatile.Write(ref _lease, $"\"{lease:c}\"");
    }

    /// <summary>
    /// Refuses later publications with this status.
    /// </summary>
    public void RefuseWith(int status)
    {
        Volatile.Write(ref _refusal, status);
    }

    /// <summary>
    /// Answers later renewals with 404, as a gateway restarted without its record does.
    /// </summary>
    public void ForgetInstances()
    {
        _forgetful = true;
    }

    public async ValueTask DisposeAsync()
    {
        await ((IWebApplication)_application).StopAsync();
        await ((IAsyncDisposable)_application).DisposeAsync();
    }

    private async Task ReceiveAsync(IHttpContext context)
    {
        if (context.Request.Path.Value != "/manifests")
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestCancelled);

        if (context.Request.Method == HttpMethod.Put)
        {
            Interlocked.Increment(ref _renewals);
            context.Response.StatusCode = _forgetful ? HttpStatusCode.NotFound : HttpStatusCode.NoContent;
            return;
        }

        if (context.Request.Method != HttpMethod.Post)
        {
            context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
            return;
        }

        _manifests.Enqueue(_json.Deserialize<ResourceManifest>(body.ToArray())!);

        if (Volatile.Read(ref _refusal) is int refusal and > 0)
        {
            context.Response.StatusCode = refusal;
            return;
        }

        byte[] answer = Encoding.UTF8.GetBytes(
            $"{{\"snapshots\":{Encoding.UTF8.GetString(Volatile.Read(ref _answer))},\"lease\":{Volatile.Read(ref _lease)}}}");
        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        await context.Response.Body.WriteAsync(answer, context.RequestCancelled);
    }
}
