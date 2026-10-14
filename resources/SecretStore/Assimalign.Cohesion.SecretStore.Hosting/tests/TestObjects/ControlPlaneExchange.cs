using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

internal sealed class ControlPlaneExchange : IHttpContext
{
    internal ControlPlaneExchange(string path, HttpMethod method, EndPoint? remoteEndPoint)
    {
        Request = new RequestData(this, path, method);
        Response = new ResponseData(this);
        ConnectionInfo = new ConnectionData(remoteEndPoint);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo { get; }
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    public CancellationToken RequestCancelled => CancellationToken.None;
    public void Cancel() { }
    public Task CancelAsync() => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await Request.Body.DisposeAsync();
        await Response.Body.DisposeAsync();
    }

    private sealed class ConnectionData : IHttpConnectionInfo
    {
        internal ConnectionData(EndPoint? remoteEndPoint)
        {
            RemoteEndPoint = remoteEndPoint;
        }

        public int LocalPort => 8443;
        public IPAddress? LocalIp => IPAddress.Loopback;
        public EndPoint? LocalEndPoint => new IPEndPoint(IPAddress.Loopback, LocalPort);
        public int RemotePort => (RemoteEndPoint as IPEndPoint)?.Port ?? 0;
        public IPAddress? RemoteIp => (RemoteEndPoint as IPEndPoint)?.Address;
        public EndPoint? RemoteEndPoint { get; }
        public CancellationToken ConnectionAborted => CancellationToken.None;
        public void Abort() { }
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
    }

    private sealed class RequestData : IHttpRequest
    {
        internal RequestData(IHttpContext context, string path, HttpMethod method)
        {
            HttpContext = context;
            Path = new HttpPath(path);
            Method = method;
        }

        public HttpHost Host => new("localhost");
        public HttpPath Path { get; }
        public HttpMethod Method { get; }
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; } = new MemoryStream();
    }

    private sealed class ResponseData : IHttpResponse
    {
        internal ResponseData(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = new MemoryStream();
    }
}
