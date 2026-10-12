using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Tests.TestObjects;

internal sealed class TestHttpContext : HttpContext
{
    private TestHttpContext(X509Certificate2? certificate, bool tls)
    {
        Request = new TestHttpRequest(this);
        Response = new TestHttpResponse(this);
        if (tls)
        {
            Features.Set<IHttpTlsConnectionFeature>(new TestTlsConnectionFeature(certificate));
        }
    }

    public override HttpVersion Version => HttpVersion.Http11;
    public override HttpRequest Request { get; }
    public override HttpResponse Response { get; }
    public override HttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public override HttpFeatureCollection Features { get; } = new();
    public override IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public override CancellationToken RequestCancelled => CancellationToken.None;
    public override void Cancel() { }
    public override Task CancelAsync() => Task.CompletedTask;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static TestHttpContext Create(X509Certificate2? certificate = null, bool tls = true) => new(certificate, tls);
}

internal sealed class TestHttpRequest : HttpRequest
{
    public TestHttpRequest(HttpContext context) => HttpContext = context;

    public override HttpContext HttpContext { get; }
    public override HttpHost Host { get; set; } = HttpHost.Empty;
    public override HttpPath Path { get; set; } = HttpPath.Root;
    public override HttpMethod Method { get; set; } = HttpMethod.Get;
    public override HttpScheme Scheme { get; set; } = HttpScheme.Https;
    public override HttpQueryCollection Query { get; } = new();
    public override HttpHeaderCollection Headers { get; } = new();
    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : HttpResponse
{
    public TestHttpResponse(HttpContext context) => HttpContext = context;

    public override HttpContext HttpContext { get; }
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public override HttpHeaderCollection Headers { get; } = new();
    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestTlsConnectionFeature : IHttpTlsConnectionFeature
{
    public TestTlsConnectionFeature(X509Certificate2? certificate) => ClientCertificate = certificate;

    public string Name => "Assimalign.Cohesion.Http.TlsConnection";
    public X509Certificate2? ClientCertificate { get; }
    public SslProtocols Protocol => SslProtocols.Tls13;
    public TlsCipherSuite CipherSuite => TlsCipherSuite.TLS_AES_128_GCM_SHA256;
    public SslApplicationProtocol ApplicationProtocol => SslApplicationProtocol.Http11;
}
