using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>A framed stream permanently bound to one endpoint's message family.</summary>
/// <remarks>The channel validates identifiers in both directions. The endpoint's session
/// owns message ordering and payload codecs. A connection cannot switch families.</remarks>
public sealed class ProtocolChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    /// <summary>Binds the stream to its endpoint's family.</summary>
    /// <param name="stream">The connected duplex stream.</param>
    /// <param name="family">The immutable family selected by the endpoint.</param>
    /// <param name="leaveOpen">Whether disposal leaves the transport stream open.</param>
    /// <exception cref="ArgumentNullException">The stream or family is null.</exception>
    public ProtocolChannel(Stream stream, ProtocolMessageFamily family, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(family);
        _stream = stream;
        _leaveOpen = leaveOpen;
        Family = family;
        Reader = new FamilyReader(ProtocolFraming.CreateReader(stream, leaveOpen: true), family);
        Writer = new FamilyWriter(ProtocolFraming.CreateWriter(stream, leaveOpen: true), family);
    }

    /// <summary>Gets the family fixed when the channel was created.</summary>
    public ProtocolMessageFamily Family { get; }

    /// <summary>Gets the reader that rejects identifiers outside the bound family.</summary>
    public IProtocolFrameReader Reader { get; }

    /// <summary>Gets the writer that rejects identifiers outside the bound family.</summary>
    public IProtocolFrameWriter Writer { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync().ConfigureAwait(false);
        await Writer.DisposeAsync().ConfigureAwait(false);
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void Validate(ProtocolMessageFamily family, ProtocolMessageType type)
    {
        if (!family.Supports(type))
        {
            throw new ProtocolException($"Message identifier {(byte)type} is not defined by endpoint family '{family.Name}'.");
        }
    }

    private sealed class FamilyReader(IProtocolFrameReader reader, ProtocolMessageFamily family) : IProtocolFrameReader
    {
        public async ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            ProtocolFrame? frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is { } value)
            {
                Validate(family, value.Type);
            }
            return frame;
        }

        public ValueTask DisposeAsync() => reader.DisposeAsync();
    }

    private sealed class FamilyWriter(IProtocolFrameWriter writer, ProtocolMessageFamily family) : IProtocolFrameWriter
    {
        public ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
        {
            Validate(family, frame.Type);
            return writer.WriteFrameAsync(frame, cancellationToken);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => writer.FlushAsync(cancellationToken);
        public ValueTask DisposeAsync() => writer.DisposeAsync();
    }
}
