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

    private sealed class RequestData : IHttpRequest
    {
        private readonly IHttpContext _context;
        private readonly string _path;
        private readonly HttpMethod _method;

        /// <summary>
        /// Initializes a new instance of the <see cref="RequestData"/> class.
        /// </summary>
        /// <param name="context">The exchange that owns the request.</param>
        /// <param name="path">The request path.</param>
        /// <param name="method">The request method.</param>
        public RequestData(IHttpContext context, string path, HttpMethod method)
        {
            _context = context;
            _path = path;
            _method = method;
        }

        public HttpHost Host => new("localhost");
        public HttpPath Path => new(_path);
        public HttpMethod Method => _method;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext => _context;
        public Stream Body { get; } = new MemoryStream();
    }

    private sealed class ResponseData : IHttpResponse
    {
        private readonly IHttpContext _context;

        /// <summary>
        /// Initializes a new instance of the <see cref="ResponseData"/> class.
        /// </summary>
        /// <param name="context">The exchange that owns the response.</param>
        public ResponseData(IHttpContext context)
        {
            _context = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext => _context;
        public Stream Body { get; set; } = new MemoryStream();
    }
}
