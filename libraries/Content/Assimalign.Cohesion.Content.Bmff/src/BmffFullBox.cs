using System;
using System.Buffers.Binary;

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
    protected void ReadFullBoxHeader(BmffStream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        var value = BmffBinary.ReadField(stream, bytes).ReadUInt32BigEndian();
        Version = (byte)(value >> 24);
        Flags = value & 0xFFFFFF;
    }

    /// <summary>Writes the version and flags.</summary>
    /// <param name="stream">The box payload output.</param>
    protected void WriteFullBoxHeader(BmffStream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, ((uint)Version << 24) | Flags);
        stream.Write(bytes);
    }
}
