using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

/// <summary>
/// Mutable <see cref="IHttpContext"/> test double: the request method is
/// settable, request/response header collections are real (so the cookie and
/// header extension paths work), and the feature collection is real (so the
/// form feature and antiforgery feature can be installed).
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext(HttpMethod? method = null)
    {
        Request = new TestHttpRequest(this, method ?? HttpMethod.Post);
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;

    public IHttpRequest Request { get; }

    public IHttpResponse Response { get; }

    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public CancellationToken RequestCancelled => CancellationToken.None;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Sets the <c>Cookie</c> request header to <c>name=value</c>.</summary>
    public void SetRequestCookie(string name, string value)
    {
        Request.Headers[HttpHeaderKey.Cookie] = $"{name}={value}";
    }

    /// <summary>Sets an arbitrary request header.</summary>
    public void SetRequestHeader(string name, string value)
    {
        Request.Headers[name] = value;
    }

    /// <summary>Installs a parsed form feature carrying a single field.</summary>
    public void SetFormField(string name, string value)
    {
        HttpFormCollection form = new();
        form.Add(name, value);
        Features.Set<IHttpFormFeature>(new TestFormFeature(form));
    }

    public void Cancel()
    {
        
    }

    public Task CancelAsync()
    {
        return Task.CompletedTask;
    }

    private sealed class TestHttpRequest : IHttpRequest
    {
        public TestHttpRequest(IHttpContext context, HttpMethod method)
        {
            HttpContext = context;
            Method = method;
        }

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method { get; set; }
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body => Stream.Null;
    }

    private sealed class TestHttpResponse : IHttpResponse
    {
        public TestHttpResponse(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = Stream.Null;
    }

    private sealed class TestFormFeature : IHttpFormFeature
    {
        private readonly IHttpFormCollection _form;

        public TestFormFeature(IHttpFormCollection form)
        {
            _form = form;
        }

        public string Name => nameof(TestFormFeature);

        public IHttpFormCollection? Form => _form;

        public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_form);
        }
    }
}
