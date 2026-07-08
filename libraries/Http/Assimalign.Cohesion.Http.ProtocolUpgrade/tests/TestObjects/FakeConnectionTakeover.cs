using System;
using System.IO;

namespace Assimalign.Cohesion.Http.ProtocolUpgrade.Tests.TestObjects;

/// <summary>
/// In-memory <see cref="IHttpConnectionTakeover"/> double standing in for the transport
/// capability. Surrenders a caller-supplied stream (usually a <see cref="MemoryStream"/> so the
/// written transition response can be inspected), records whether <see cref="TakeOver"/> ran,
/// and enforces the contract's one-shot rule.
/// </summary>
internal sealed class FakeConnectionTakeover : IHttpConnectionTakeover
{
    private readonly Stream _stream;

    public FakeConnectionTakeover(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Whether <see cref="TakeOver"/> has been invoked.</summary>
    public bool TakenOver { get; private set; }

    /// <inheritdoc />
    public Stream TakeOver()
    {
        if (TakenOver)
        {
            throw new InvalidOperationException("The connection has already been taken over for this exchange.");
        }

        TakenOver = true;
        return _stream;
    }
}
