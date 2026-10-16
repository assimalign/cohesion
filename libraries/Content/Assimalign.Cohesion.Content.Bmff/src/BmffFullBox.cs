using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A box whose payload starts with an eight-bit version and 24-bit flags.</summary>
// Deviates from the repo interface-first rule per the owner's Stage 2 composite-design requirement.
public abstract class BmffFullBox : BmffBox
{
    private uint _flags;

    /// <summary>Gets or sets the version byte.</summary>
    public byte Version { get; set; }
    /// <summary>Gets or sets the 24-bit flags.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value exceeds 24 bits.</exception>
    public uint Flags
    {
        get => _flags;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 0xFFFFFFu);
            _flags = value;
        }
    }

    /// <summary>Reads the version and flags from a bounded payload.</summary>
    /// <param name="stream">The box payload.</param>
    /// <exception cref="ContentFormatException">The prefix is truncated.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.OperationCanceledException">The payload window's cancellation token was canceled.</exception>
    protected void ReadFullBoxHeader(BmffStream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        var value = BmffBinary.ReadField(stream, bytes).ReadUInt32BigEndian();
        Version = (byte)(value >> 24);
        Flags = value & 0xFFFFFF;
    }

    /// <summary>Writes the version and flags.</summary>
    /// <param name="stream">The box payload output.</param>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    /// <exception cref="System.OperationCanceledException">The payload window's cancellation token was canceled.</exception>
    /// <exception cref="ContentFormatException">The output window cannot contain the full-box prefix.</exception>
    protected void WriteFullBoxHeader(BmffStream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ((uint)Version << 24) | Flags);
        stream.Write(bytes);
    }
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" version={Version} flags=0x{Flags:X6}"));
    }
}
