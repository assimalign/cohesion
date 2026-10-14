using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;

namespace Assimalign.Cohesion.Web.Server.Tests;

public class WebClientFaultInterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Server] - Server defaults: The client-fault interceptor publishes the control's report on a request that can have a body")]
    public void BeforeResponse_ClientFault_ShouldPublishTheControlReport()
    {
        // Arrange — #1340: the transport reports a client fault on the exchange control, which exists only
        // in the response phase, so the interceptor takes part in it for a request that can fault.
        WebClientFaultInterceptor interceptor = new();

        HttpHeaderCollection requestHeaders = new();
        requestHeaders[HttpHeaderKey.TransferEncoding] = "chunked";
        HttpFeatureCollection features = new();
        HttpExchangeInterceptorRequestContext request = CreateRequestContext(HttpVersion.Http11, requestHeaders, features);
        ClientFaultControl control = new() { ClientFaultStatusCode = HttpStatusCode.BadRequest };

        // Act — invoke the server-owned interceptor.
        interceptor.AfterRequestHead(request);
        request.ResponseInterceptors.ShouldHaveSingleItem().ShouldBeSameAs(interceptor);
        interceptor.BeforeResponse(new HttpExchangeInterceptorResponseContext
        {
            Version = HttpVersion.Http11,
            Headers = new HttpHeaderCollection(),
            Features = features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            ResponseBody = System.IO.Stream.Null,
            Control = control,
        });

        // Assert — the feature reads the control, so a status latched later is reported too.
        IWebClientFaultFeature feature = features.Get<IWebClientFaultFeature>().ShouldNotBeNull();
        feature.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        control.ClientFaultStatusCode = HttpStatusCode.RequestEntityTooLarge;
        feature.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Server] - Server defaults: The client-fault interceptor should leave a request that cannot have a body on the fast path")]
    [InlineData("1.1")]
    [InlineData("2")]
    [InlineData("3")]
    public void ClientFaultInterceptor_OnRequestThatCannotHaveABody_ShouldNotTakeTheResponsePhase(string version)
    {
        // Arrange — the transport found that nothing follows the head: no body can fault while it is read.
        WebClientFaultInterceptor interceptor = new();
        HttpExchangeInterceptorRequestContext request = CreateRequestContext(ToVersion(version), new HttpHeaderCollection(), new HttpFeatureCollection(), canHaveBody: false);

        // Act
        interceptor.AfterRequestHead(request);

        // Assert
        request.ResponseInterceptors.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Server] - Server defaults: The client-fault interceptor should take the response phase for a request that can have a body on every version")]
    [InlineData("1.1")]
    [InlineData("2")]
    [InlineData("3")]
    public void ClientFaultInterceptor_OnRequestThatCanHaveABody_ShouldTakeTheResponsePhase(string version)
    {
        // Arrange — #1378: an HTTP/2 or HTTP/3 request need not declare its body, so the head carries no
        // Content-Length or Transfer-Encoding here; the transport's signal alone decides.
        WebClientFaultInterceptor interceptor = new();
        HttpExchangeInterceptorRequestContext request = CreateRequestContext(ToVersion(version), new HttpHeaderCollection(), new HttpFeatureCollection(), canHaveBody: true);

        // Act
        interceptor.AfterRequestHead(request);

        // Assert
        request.ResponseInterceptors.ShouldHaveSingleItem().ShouldBeSameAs(interceptor);
    }

    private static HttpVersion ToVersion(string version) => version switch
    {
        "2" => HttpVersion.Http20,
        "3" => HttpVersion.Http30,
        _ => HttpVersion.Http11,
    };

    private static HttpExchangeInterceptorRequestContext CreateRequestContext(
        HttpVersion version,
        HttpHeaderCollection headers,
        HttpFeatureCollection features,
        bool canHaveBody = true)
    {
        return new HttpExchangeInterceptorRequestContext
        {
            Version = version,
            Method = HttpMethod.Post,
            Path = new HttpPath("/upload"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            CanHaveBody = canHaveBody,
            Headers = headers.AsReadOnly(),
            Features = features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        };
    }

    /// <summary>An exchange control that reports a client fault and offers no wire mechanism.</summary>
    private sealed class ClientFaultControl : IHttpExchangeControl
    {
        public HttpStatusCode? ClientFaultStatusCode { get; set; }

        public bool HasResponseStarted => false;

        public bool CanWriteInterimResponse => false;

        public ValueTask WriteInterimResponseAsync(HttpStatusCode statusCode, IHttpHeaderCollection? headers = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public bool CanTakeOver => false;

        public System.IO.Stream TakeOver() => throw new NotSupportedException();

        public bool CanAcceptTunnel => false;

        public ValueTask<System.IO.Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

}
