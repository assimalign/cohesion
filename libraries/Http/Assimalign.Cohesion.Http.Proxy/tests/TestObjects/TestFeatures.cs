using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// A protocol upgrade or <c>CONNECT</c> whose accepted stream is one end of a socket pair. Captures
/// the response fields staged when it was accepted, as a transport would write them.
/// </summary>
internal sealed class TestProtocolUpgrade : IHttpProtocolUpgrade, IHttpProtocolUpgradeFeature
{
    private readonly IHttpContext _context;
    private readonly Stream _stream;

    public TestProtocolUpgrade(IHttpContext context, HttpProtocolUpgradeKind kind, string? protocol, Stream stream)
    {
        _context = context;
        _stream = stream;
        Kind = kind;
        Protocol = protocol;
    }

    public string Name => "TestProtocolUpgrade";

    public IHttpProtocolUpgrade Upgrade => this;

    public HttpProtocolUpgradeKind Kind { get; }

    public string? Protocol { get; }

    public bool Accepted { get; private set; }

    public Dictionary<string, string> AcceptedHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Accepted = true;
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in _context.Response.Headers)
        {
            AcceptedHeaders[header.Key.Value] = header.Value.Value;
        }

        return ValueTask.FromResult(_stream);
    }
}

/// <summary>
/// An extended <c>CONNECT</c> (RFC 8441) whose accepted stream is one end of a socket pair.
/// </summary>
internal sealed class TestExtendedConnectFeature : IHttpExtendedConnectFeature
{
    private readonly IHttpContext _context;
    private readonly Stream _stream;

    public TestExtendedConnectFeature(IHttpContext context, string protocol, Stream stream)
    {
        _context = context;
        _stream = stream;
        Protocol = protocol;
    }

    public string Name => "TestExtendedConnect";

    public string Protocol { get; }

    public bool Accepted { get; private set; }

    public Dictionary<string, string> AcceptedHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Accepted = true;
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in _context.Response.Headers)
        {
            AcceptedHeaders[header.Key.Value] = header.Value.Value;
        }

        return ValueTask.FromResult(_stream);
    }
}

/// <summary>
/// A response-streaming feature that records what reached the wire: the head at the first write,
/// each write and flush, and completion.
/// </summary>
internal sealed class TestResponseStreamingFeature : IHttpResponseStreamingFeature
{
    private readonly IHttpContext _context;

    public TestResponseStreamingFeature(IHttpContext context)
    {
        _context = context;
    }

    public string Name => "TestResponseStreaming";

    public bool HasStarted { get; private set; }

    public bool Completed { get; private set; }

    public int Flushes { get; private set; }

    public int? StartedStatus { get; private set; }

    public Dictionary<string, string> StartedHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MemoryStream Body { get; } = new();

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Start();
        Body.Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        Start();
        Flushes++;
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Start();
        Completed = true;
        return ValueTask.CompletedTask;
    }

    private void Start()
    {
        if (HasStarted)
        {
            return;
        }

        HasStarted = true;
        StartedStatus = _context.Response.StatusCode;
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in _context.Response.Headers)
        {
            StartedHeaders[header.Key.Value] = header.Value.Value;
        }
    }
}
