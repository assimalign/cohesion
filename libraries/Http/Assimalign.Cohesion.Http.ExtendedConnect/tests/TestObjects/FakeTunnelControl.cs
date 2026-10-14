using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.ExtendedConnect.Tests.TestObjects;

/// <summary>
/// An <see cref="IHttpExchangeControl"/> double standing in for the HTTP/2 and HTTP/3 transports'
/// exchange control: it offers only the extended CONNECT tunnel accept, hands out a fixed tunnel
/// stream, and records each accept and the token it was given. Construct it with
/// <c>canAcceptTunnel: false</c> to model a control that cannot accept one (HTTP/1.1, or an exchange
/// already cancelled).
/// </summary>
internal sealed class FakeTunnelControl : IHttpExchangeControl
{
    private readonly Stream _tunnel;
    private readonly bool _canAcceptTunnel;

    public FakeTunnelControl(Stream tunnel, bool canAcceptTunnel = true)
    {
        _tunnel = tunnel;
        _canAcceptTunnel = canAcceptTunnel;
    }

    /// <summary>Gets how many times <see cref="AcceptTunnelAsync"/> ran.</summary>
    public int AcceptCount { get; private set; }

    /// <summary>Gets the token the last accept was given.</summary>
    public CancellationToken LastToken { get; private set; }

    public bool HasResponseStarted => false;

    public bool CanWriteInterimResponse => false;

    public ValueTask WriteInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The fake control does not support interim responses.");

    public bool CanTakeOver => false;

    public Stream TakeOver() => throw new InvalidOperationException("The fake control cannot take its connection over.");

    public bool CanAcceptTunnel => _canAcceptTunnel && AcceptCount == 0;

    public ValueTask<Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default)
    {
        AcceptCount++;
        LastToken = cancellationToken;
        return ValueTask.FromResult(_tunnel);
    }
}
