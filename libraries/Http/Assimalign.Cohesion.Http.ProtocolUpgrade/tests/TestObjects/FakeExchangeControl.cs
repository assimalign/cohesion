using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.ProtocolUpgrade.Tests.TestObjects;

/// <summary>
/// In-memory <see cref="IHttpExchangeControl"/> double standing in for the transport's
/// per-exchange control surface. Surrenders a caller-supplied stream (usually a
/// <see cref="MemoryStream"/> so the written transition response can be inspected), records
/// whether <see cref="TakeOver"/> ran and enforces
/// the contract's one-shot takeover rule. Construct with <c>canTakeOver: false</c> to model a
/// control that can never surrender its connection (an HTTP/2 or HTTP/3 multiplexed exchange).
/// </summary>
internal sealed class FakeExchangeControl : IHttpExchangeControl
{
    private readonly Stream _stream;
    private readonly bool _canTakeOver;

    public FakeExchangeControl(Stream stream, bool canTakeOver = true)
    {
        _stream = stream;
        _canTakeOver = canTakeOver;
    }

    /// <summary>Whether <see cref="TakeOver"/> has been invoked.</summary>
    public bool TakenOver { get; private set; }

    /// <inheritdoc />

    /// <inheritdoc />
    public bool HasResponseStarted => false;

    /// <inheritdoc />
    public bool CanWriteInterimResponse => false;

    /// <inheritdoc />
    public ValueTask WriteInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The fake exchange control does not support interim responses.");

    /// <inheritdoc />
    public bool CanTakeOver => _canTakeOver && !TakenOver;

    /// <inheritdoc />
    public Stream TakeOver()
    {
        if (!_canTakeOver)
        {
            throw new InvalidOperationException("This exchange's connection cannot be taken over.");
        }

        if (TakenOver)
        {
            throw new InvalidOperationException("The connection has already been taken over for this exchange.");
        }

        TakenOver = true;
        return _stream;
    }
}
