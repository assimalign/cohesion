using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// Shared composition and wire helpers for the security-headers suites.
/// </summary>
internal static class SecurityHeadersTestHost
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates an in-memory HTTP/1.1 factory. With <paramref name="streaming"/>, the transport installs
    /// the response-streaming feature, so handlers can commit the head themselves.
    /// </summary>
    public static WebApplicationTestFactory CreateFactory(bool streaming = false)
    {
        WebApplicationTestFactory factory = new();

        if (streaming)
        {
            factory.Builder.Server.UseServer(options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        }

        return factory;
    }

    /// <summary>Gets a response field as the client received it, or <see langword="null"/> when absent.</summary>
    public static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            || response.Content.Headers.TryGetValues(name, out values))
        {
            return string.Join(", ", values);
        }

        return null;
    }

    /// <summary>A route handler that answers 200 with a small text body.</summary>
    public static RouterRouteHandler Ok(string body = "ok") => new(context => WriteTextAsync(context, body));

    /// <summary>Writes <paramref name="body"/> to the buffered response body.</summary>
    public static async Task WriteTextAsync(IHttpContext context, string body, string contentType = "text/plain; charset=utf-8")
    {
        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.ContentType] = contentType;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        await context.Response.Body.WriteAsync(bytes, context.RequestCancelled);
    }

    /// <summary>Writes <paramref name="body"/> through the response-streaming feature, committing the head.</summary>
    public static async Task StreamTextAsync(IHttpContext context, string body)
    {
        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;
        await streaming.WriteAsync(Encoding.UTF8.GetBytes(body), context.RequestCancelled);
        await streaming.FlushAsync(context.RequestCancelled);
    }

    /// <summary>Reads the exchange's nonce, the way a handler does before stamping it on markup.</summary>
    public static string ReadNonce(IHttpContext context)
    {
        return context.Features.Get<ISecurityHeadersFeature>()?.Nonce
            ?? throw new InvalidOperationException("UseSecurityHeaders installed no ISecurityHeadersFeature.");
    }

    /// <summary>Creates an in-memory site holding the given files.</summary>
    public static InMemoryFileSystem CreateSite(params (string Path, string Content)[] files)
    {
        InMemoryFileSystem fileSystem = new(new InMemoryFileSystemOptions
        {
            Name = "security-headers-site",
        });

        foreach ((string path, string content) in files)
        {
            IFileSystemFile file = fileSystem.CreateFile(path);
            using Stream stream = file.Open(FileMode.Open, FileAccess.Write);
            byte[] payload = Encoding.UTF8.GetBytes(content);
            stream.Write(payload, 0, payload.Length);
        }

        return fileSystem;
    }
}

/// <summary>
/// An application-supplied <see cref="ISecurityHeadersFeature"/> with a fixed nonce, counting how often
/// the middleware or a handler reads it.
/// </summary>
internal sealed class FixedNonceFeature : ISecurityHeadersFeature
{
    private readonly string _nonce;
    private int _reads;

    public FixedNonceFeature(string nonce)
    {
        _nonce = nonce;
    }

    public string Name => nameof(ISecurityHeadersFeature);

    public string Nonce
    {
        get
        {
            Interlocked.Increment(ref _reads);
            return _nonce;
        }
    }

    public int Reads => Volatile.Read(ref _reads);
}
