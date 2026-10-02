using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;

/// <summary>
/// Shared plumbing for the end-to-end suites: a terminal handler that appends cookies, and a request
/// helper that returns the response's <c>Set-Cookie</c> fields. Every request flows the real pipeline of
/// a <see cref="WebApplicationTestFactory"/> over the in-memory transport.
/// </summary>
internal static class CookiePolicyTestHost
{
    /// <summary>The per-test budget for a request round trip.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A fixed instant for the lifetime-cap suites.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The headers a TLS-terminating proxy adds: the client address and the scheme the client used. The
    /// in-memory transport's peer is a local transport, which <c>UseForwardedHeaders</c> trusts by default.
    /// </summary>
    public static readonly (string Name, string Value)[] ForwardedHttps =
    [
        ("X-Forwarded-For", "203.0.113.9"),
        ("X-Forwarded-Proto", "https"),
    ];

    /// <summary>
    /// Registers the terminal handler: it runs <paramref name="write"/> and answers <c>200</c>.
    /// </summary>
    public static void UseCookieWriter(IWebApplicationPipelineBuilder pipeline, Action<IHttpContext> write)
    {
        pipeline.Use((context, next) =>
        {
            write(context);
            context.Response.StatusCode = HttpStatusCode.Ok;
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Sends <c>GET /</c> with the given headers and returns the response's <c>Set-Cookie</c> fields.
    /// </summary>
    public static Task<IReadOnlyList<SetCookieField>> SendAsync(
        HttpClient client,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] headers)
        => SendAsync(client, "/", cancellationToken, headers);

    /// <summary>
    /// Sends <c>GET</c> to <paramref name="path"/> with the given headers and returns the response's
    /// <c>Set-Cookie</c> fields. The response must be <c>200</c>: a handler fault surfaces as a failure here.
    /// </summary>
    public static async Task<IReadOnlyList<SetCookieField>> SendAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] headers)
    {
        // A hand-built request defaults to HTTP/1.1; take the client's version policy so an HTTP/2
        // factory's prior-knowledge client speaks HTTP/2.
        using HttpRequestMessage request = new(NetHttpMethod.Get, path)
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };

        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value).ShouldBeTrue();
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);

        return SetCookieField.ReadAll(response);
    }
}
