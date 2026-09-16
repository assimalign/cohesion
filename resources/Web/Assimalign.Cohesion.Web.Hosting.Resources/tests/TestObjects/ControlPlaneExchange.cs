using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using EndPoint = System.Net.EndPoint;
using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

internal sealed class ControlPlaneExchange : IHttpContext
{
    internal ControlPlaneExchange(string path, HttpMethod method)
    {
        Request = new RequestData(this, path, method);
        Response = new ResponseData(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    internal ConnectionData Connection { get; } = new();
    public IHttpConnectionInfo ConnectionInfo => Connection;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    public CancellationToken RequestCancelled { get; set; }
    public void Cancel() { }
    public Task CancelAsync() => Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        await Request.Body.DisposeAsync();
        await Response.Body.DisposeAsync();
    }

    internal sealed class ConnectionData : IHttpConnectionInfo
    {
        public int LocalPort { get; set; }
        public IPAddress? LocalIp => IPAddress.Loopback;
        public EndPoint? LocalEndPoint => new IPEndPoint(IPAddress.Loopback, LocalPort);
        public int RemotePort => 0;
        public IPAddress? RemoteIp => IPAddress.Loopback;
        public EndPoint? RemoteEndPoint => null;
        public CancellationToken ConnectionAborted => CancellationToken.None;
        public void Abort() { }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
    }

    private sealed class RequestData(IHttpContext context, string path, HttpMethod method) : IHttpRequest
    {
        public HttpHost Host => new("localhost");
        public HttpPath Path => new(path);
        public HttpMethod Method => method;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext => context;
        public Stream Body { get; } = new MemoryStream();
    }

    private sealed class ResponseData(IHttpContext context) : IHttpResponse
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext => context;
        public Stream Body { get; set; } = new MemoryStream();
    }
}
