using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// A query parameter with an empty name (#1323), such as <c>?=1</c>, on every transport. A query key is
/// never empty, so <c>HttpQuery.Parse</c> skips the parameter and the request is served with the
/// parameters that have names; before #1323 the parse threw while the transport read the head, and
/// the client lost its connection or its stream.
/// </summary>
public class HttpQueryEmptyNameTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Query: A parameter with an empty name should be skipped on HTTP/1.1")]
    [InlineData("=1", "")]
    [InlineData("&=", "")]
    [InlineData("a=1&=2&b=3", "a=1;b=3")]
    public async Task Http1_OnParameterWithEmptyName_ShouldSkipParameter(string query, string expected)
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(new TestConnection(
            HttpProtocolPayloadFactory.CreateHttp1Request($"GET /x?{query} HTTP/1.1\r\nHost: api.test\r\n\r\n"))));

        // Act
        IHttpContext? context = await ReadFirstContextOrNullAsync(options);

        // Assert
        AssertServedWithoutUnnamedParameter(context, expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Query: A parameter with an empty name should be skipped on HTTP/2")]
    [InlineData("=1", "")]
    [InlineData("&=", "")]
    [InlineData("a=1&=2&b=3", "a=1;b=3")]
    public async Task Http2_OnParameterWithEmptyName_ShouldSkipParameter(string query, string expected)
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(new TestConnection(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", $"/x?{query}", "https", "api.test"))));

        // Act
        IHttpContext? context = await ReadFirstContextOrNullAsync(options);

        // Assert
        AssertServedWithoutUnnamedParameter(context, expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Query: A parameter with an empty name should be skipped on HTTP/3")]
    [InlineData("=1", "")]
    [InlineData("&=", "")]
    [InlineData("a=1&=2&b=3", "a=1;b=3")]
    public async Task Http3_OnParameterWithEmptyName_ShouldSkipParameter(string query, string expected)
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(new TestConnection(
            HttpProtocolPayloadFactory.CreateHttp3Request("GET", $"/x?{query}", "https", "api.test")))));

        // Act
        IHttpContext? context = await ReadFirstContextOrNullAsync(options);

        // Assert
        AssertServedWithoutUnnamedParameter(context, expected);
    }

    private static void AssertServedWithoutUnnamedParameter(IHttpContext? context, string expected)
    {
        context.ShouldNotBeNull("the request should be dispatched, not fail while its head is read");
        IHttpRequest request = context!.Request;
        request.Path.Value.ShouldBe("/x");
        string.Join(
                ";",
                request.Query
                    .OrderBy(parameter => parameter.Key.Value, StringComparer.Ordinal)
                    .Select(parameter => $"{parameter.Key.Value}={parameter.Value.Value}"))
            .ShouldBe(expected);
    }

    private static async Task<IHttpContext?> ReadFirstContextOrNullAsync(HttpConnectionListenerOptions options)
    {
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connection.ReceiveAsync().GetAsyncEnumerator();
        return await enumerator.MoveNextAsync() ? enumerator.Current : null;
    }
}
