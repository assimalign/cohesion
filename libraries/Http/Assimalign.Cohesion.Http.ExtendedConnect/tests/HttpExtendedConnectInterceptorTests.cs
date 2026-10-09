using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.ExtendedConnect.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ExtendedConnect.Tests;

/// <summary>
/// The extended CONNECT interceptor (#1368): its request-head hook installs the feature for an HTTP/2
/// or HTTP/3 extended CONNECT the transport validated and joins that exchange's response phase, and
/// its response hook binds the feature to the exchange control that accepts the tunnel. Driven the
/// way a transport drives it, over hand-built interceptor contexts.
/// </summary>
public class HttpExtendedConnectInterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: It declares the request scope only, so it is never in every exchange's response phase")]
    public void Scopes_ShouldBeRequestOnly()
    {
        // Arrange
        IHttpExchangeInterceptor interceptor = HttpExtendedConnect.CreateInterceptor();

        // Act / Assert
        interceptor.Scopes.ShouldBe(HttpInterceptorScopes.Request);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: An HTTP/2 or HTTP/3 extended CONNECT installs the feature and joins that exchange's response phase")]
    [InlineData(HttpVersion.Http20)]
    [InlineData(HttpVersion.Http30)]
    public void AfterRequestHead_OnExtendedConnect_ShouldInstallFeatureAndJoinResponsePhase(HttpVersion version)
    {
        // Arrange
        IHttpExchangeInterceptor interceptor = HttpExtendedConnect.CreateInterceptor();
        HttpExchangeInterceptorRequestContext context = CreateHeadContext(version, HttpMethod.Connect, "websocket");

        // Act
        interceptor.AfterRequestHead(context);

        // Assert
        IHttpExtendedConnectFeature? feature = context.Features.Get<IHttpExtendedConnectFeature>();
        feature.ShouldNotBeNull();
        feature!.Protocol.ShouldBe("websocket");
        context.ResponseInterceptors.ShouldBe([interceptor]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: Any other request installs nothing and stays out of the response phase")]
    [InlineData(HttpVersion.Http20, "CONNECT", null)]
    [InlineData(HttpVersion.Http30, "CONNECT", null)]
    [InlineData(HttpVersion.Http20, "GET", null)]
    [InlineData(HttpVersion.Http20, "GET", "websocket")]
    [InlineData(HttpVersion.Http11, "CONNECT", "websocket")]
    [InlineData(HttpVersion.Http11, "GET", null)]
    [InlineData(HttpVersion.Http20, "CONNECT", "")]
    public void AfterRequestHead_OnAnyOtherRequest_ShouldInstallNothing(HttpVersion version, string method, string? protocol)
    {
        // Arrange — a classic CONNECT, an ordinary request, and hand-built contexts no transport sends.
        IHttpExchangeInterceptor interceptor = HttpExtendedConnect.CreateInterceptor();
        HttpExchangeInterceptorRequestContext context = CreateHeadContext(version, HttpMethod.GetCanonicalizedValue(method), protocol);

        // Act
        interceptor.AfterRequestHead(context);

        // Assert
        context.Features.Get<IHttpExtendedConnectFeature>().ShouldBeNull();
        context.ResponseInterceptors.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: A bound feature accepts through the exchange control and passes the token on")]
    public async Task AcceptAsync_OnBoundFeature_ShouldAcceptThroughTheControl()
    {
        // Arrange
        await using MemoryStream tunnel = new();
        FakeTunnelControl control = new(tunnel);
        using CancellationTokenSource cancellation = new();
        FakeHttpContext context = new();
        RunInterceptors(context, HttpVersion.Http20, control);

        // Act
        Stream accepted = await context.ExtendedConnect!.AcceptAsync(cancellation.Token);

        // Assert
        accepted.ShouldBeSameAs(tunnel);
        control.AcceptCount.ShouldBe(1);
        control.LastToken.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: A second accept reaches the control, whose one-shot rule decides it")]
    public async Task AcceptAsync_OnSecondCall_ShouldDeferToTheControl()
    {
        // Arrange — the feature keeps no accept rule of its own: every guard is the transport's.
        FakeTunnelControl control = new(Stream.Null);
        FakeHttpContext context = new();
        RunInterceptors(context, HttpVersion.Http30, control);
        IHttpExtendedConnectFeature feature = context.ExtendedConnect!;

        // Act
        await feature.AcceptAsync();
        await feature.AcceptAsync();

        // Assert
        control.AcceptCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: A control that cannot accept a tunnel removes the feature")]
    public void BeforeResponse_OnControlThatCannotAcceptTunnel_ShouldRemoveFeature()
    {
        // Arrange
        FakeHttpContext context = new();

        // Act
        RunInterceptors(context, HttpVersion.Http20, new FakeTunnelControl(Stream.Null, canAcceptTunnel: false));

        // Assert
        context.IsExtendedConnect.ShouldBeFalse();
        context.ExtendedConnect.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: No exchange control removes the feature")]
    public void BeforeResponse_OnNoControl_ShouldRemoveFeature()
    {
        // Arrange
        FakeHttpContext context = new();

        // Act
        RunInterceptors(context, HttpVersion.Http30, control: null);

        // Assert
        context.ExtendedConnect.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: A feature never bound to a control throws InvalidOperationException on accept")]
    public async Task AcceptAsync_OnUnboundFeature_ShouldThrowInvalidOperationException()
    {
        // Arrange — the request hook ran, but no response phase ever bound the feature.
        IHttpExchangeInterceptor interceptor = HttpExtendedConnect.CreateInterceptor();
        HttpExchangeInterceptorRequestContext context = CreateHeadContext(HttpVersion.Http20, HttpMethod.Connect, "websocket");
        interceptor.AfterRequestHead(context);
        IHttpExtendedConnectFeature feature = context.Features.Get<IHttpExtendedConnectFeature>()!;

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(() => feature.AcceptAsync().AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.ExtendedConnect] - Interceptor: A response hook with no installed feature leaves the exchange untouched")]
    public void BeforeResponse_OnNoFeature_ShouldDoNothing()
    {
        // Arrange
        IHttpExchangeInterceptor interceptor = HttpExtendedConnect.CreateInterceptor();
        FakeTunnelControl control = new(Stream.Null);
        HttpFeatureCollection features = new();

        // Act
        interceptor.BeforeResponse(CreateResponseContext(HttpVersion.Http20, features, control));

        // Assert
        features.Get<IHttpExtendedConnectFeature>().ShouldBeNull();
        control.AcceptCount.ShouldBe(0);
    }

    /// <summary>
    /// Drives the interceptor the way a transport does for an extended CONNECT: the request hook over
    /// a parse-time head context, then the response hook of every interceptor the exchange's response
    /// phase runs, over a response context sharing the same feature collection.
    /// </summary>
    private static void RunInterceptors(FakeHttpContext context, HttpVersion version, IHttpExchangeControl? control)
    {
        IHttpExchangeInterceptor interceptor = HttpExtendedConnect.CreateInterceptor();
        HttpExchangeInterceptorRequestContext headContext = CreateHeadContext(version, HttpMethod.Connect, "websocket", context.Features);
        interceptor.AfterRequestHead(headContext);

        HttpExchangeInterceptorResponseContext responseContext = CreateResponseContext(version, context.Features, control);
        List<IHttpExchangeInterceptor> responsePhase = new();

        if ((interceptor.Scopes & HttpInterceptorScopes.Response) != 0)
        {
            responsePhase.Add(interceptor);
        }

        foreach (IHttpExchangeInterceptor added in headContext.ResponseInterceptors)
        {
            if (!responsePhase.Contains(added))
            {
                responsePhase.Add(added);
            }
        }

        foreach (IHttpExchangeInterceptor participant in responsePhase)
        {
            participant.BeforeResponse(responseContext);
        }
    }

    private static HttpExchangeInterceptorRequestContext CreateHeadContext(
        HttpVersion version,
        HttpMethod method,
        string? protocol,
        IHttpFeatureCollection? features = null) => new()
        {
            Version = version,
            Method = method,
            Path = new HttpPath("/chat"),
            Scheme = HttpScheme.Https,
            Host = new HttpHost("api.test"),
            Protocol = protocol,
            Headers = new HttpHeaderCollection().AsReadOnly(),
            Features = features ?? new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        };

    private static HttpExchangeInterceptorResponseContext CreateResponseContext(
        HttpVersion version,
        IHttpFeatureCollection features,
        IHttpExchangeControl? control) => new()
        {
            Version = version,
            Headers = new HttpHeaderCollection(),
            Features = features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            ResponseBody = Stream.Null,
            Control = control,
        };
}
