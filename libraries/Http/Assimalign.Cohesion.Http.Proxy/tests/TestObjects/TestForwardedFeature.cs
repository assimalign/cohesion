using System.Net;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// The client identity a trusted proxy in front of this one reported, as Web.ForwardedHeaders would
/// attach it.
/// </summary>
internal sealed class TestForwardedFeature : IHttpForwardedFeature
{
    public string Name => "TestForwarded";

    public HttpScheme Scheme { get; init; }

    public HttpHost Host { get; init; }

    public EndPoint? RemoteEndPoint => RemoteIp is null ? null : new IPEndPoint(RemoteIp, RemotePort);

    public IPAddress? RemoteIp { get; init; }

    public int RemotePort { get; init; }

    public HttpScheme OriginalScheme { get; init; }

    public HttpHost OriginalHost { get; init; }

    public EndPoint? OriginalRemoteEndPoint { get; init; }

    public int TrustedHopCount { get; init; }
}
