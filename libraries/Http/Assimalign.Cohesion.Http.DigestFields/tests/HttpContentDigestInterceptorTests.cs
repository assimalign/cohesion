using System;
using System.IO;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpContentDigestInterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A matching Content-Digest passes and replays the body")]
    public void AfterRequestBody_Match_ReplaysBody()
    {
        byte[] content = Encoding.UTF8.GetBytes("payload that matches its digest");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();

        Stream result = verifier.AfterRequestBody(context, new MemoryStream(content));

        using var read = new MemoryStream();
        result.CopyTo(read);
        read.ToArray().ShouldBe(content);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A mismatched Content-Digest is rejected with 400")]
    public void AfterRequestBody_Mismatch_Rejects400()
    {
        byte[] declared = Encoding.UTF8.GetBytes("the original payload");
        byte[] actual = Encoding.UTF8.GetBytes("the tampered payload");
        string digest = HttpDigestField.ForContent(declared, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();

        HttpRequestRejectedException ex = Should.Throw<HttpRequestRejectedException>(
            () => verifier.AfterRequestBody(context, new MemoryStream(actual)));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A malformed Content-Digest is rejected with 400")]
    public void AfterRequestBody_Malformed_Rejects400()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext("sha-256=12345");
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();

        HttpRequestRejectedException ex = Should.Throw<HttpRequestRejectedException>(
            () => verifier.AfterRequestBody(context, new MemoryStream(new byte[] { 1, 2, 3 })));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: No Content-Digest passes through unchanged")]
    public void AfterRequestBody_NoDigest_PassesThrough()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(contentDigest: null);
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });

        Stream result = verifier.AfterRequestBody(context, body);

        result.ShouldBeSameAs(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: A deprecated-only Content-Digest passes through unverified")]
    public void AfterRequestBody_DeprecatedOnly_PassesThrough()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext("md5=:1B2M2Y8AsgTpgAmY7PhCfg==:");
        IHttpExchangeInterceptor verifier = HttpDigestFields.CreateContentDigestVerifier();
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });

        Stream result = verifier.AfterRequestBody(context, body);

        result.ShouldBeSameAs(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Verifier: Disposing the replay stream disposes the original body")]
    public void AfterRequestBody_ReplayDisposal_DisposesOriginal()
    {
        byte[] content = Encoding.UTF8.GetBytes("owned body");
        string digest = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize();
        HttpExchangeInterceptorRequestContext context = CreateContext(digest);
        var original = new TrackingStream(content);

        Stream result = HttpDigestFields.CreateContentDigestVerifier().AfterRequestBody(context, original);
        result.Dispose();

        original.IsDisposed.ShouldBeTrue();
    }

    private static HttpExchangeInterceptorRequestContext CreateContext(string? contentDigest)
    {
        var headers = new HttpHeaderCollection();
        if (contentDigest is not null)
        {
            headers.Add(HttpHeaderKey.ContentDigest, contentDigest);
        }

        return new HttpExchangeInterceptorRequestContext
        {
            Version = HttpVersion.Http11,
            Method = HttpMethod.Post,
            Path = new HttpPath("/upload"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = headers.AsReadOnly(),
            Features = new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        };
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] content) : base(content, writable: false)
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
