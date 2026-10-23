using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The MovieHeader box; ISO/IEC 14496-12 8.2.2.</summary>
[global::System.Diagnostics.DebuggerDisplay("{DebuggerDisplay,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class MovieHeaderBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a MovieHeaderBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="size">The original total size.</param>
    public MovieHeaderBox(long size, long offset) { SetOriginalLocation(offset, size); }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.MovieHeader;

    /// <summary>Gets or sets creation time in whole UTC seconds since 1904-01-01.</summary>
    public ulong CreationTime { get; set; }
    /// <summary>Gets or sets modification time in whole UTC seconds since 1904-01-01.</summary>
    public ulong ModificationTime { get; set; }
    /// <summary>Gets or sets the nonzero time units per second.</summary>
    public uint TimeScale { get; set; } = 1000;
    /// <summary>Gets or sets the duration in timescale units, including the all-ones unknown value.</summary>
    public ulong Duration { get; set; }
    /// <summary>Gets or sets the playback rate.</summary>
    public FixedPoint16x16 Rate { get; set; } = FixedPoint16x16.FromRaw(0x10000);
    /// <summary>Gets or sets the playback volume.</summary>
    public FixedPoint8x8 Volume { get; set; } = FixedPoint8x8.FromRaw(0x100);
    /// <summary>Gets or sets the ten reserved bytes.</summary>
    public byte[] Reserved { get; set; } = new byte[10];
    /// <summary>Gets or sets the nine raw matrix words (16.16 and 2.30 as defined by the specification).</summary>
    public int[] Matrix { get; set; } = [0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000];
    /// <summary>Gets or sets the 24 predefined bytes.</summary>
    public byte[] PreDefined { get; set; } = new byte[24];
    /// <summary>Gets or sets the next unused track identifier.</summary>
    public uint NextTrackId { get; set; } = 1;

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 1, 0, stream.Offset);
        CreationTime = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        ModificationTime = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        TimeScale = BmffFields.U32(stream);
        Duration = BmffFields.Sized(stream, Version == 1 ? 8 : 4);
        Rate = FixedPoint16x16.FromRaw(BmffFields.I32(stream));
        Volume = FixedPoint8x8.FromRaw(BmffFields.I16(stream));
        Reserved = BmffFields.Bytes(stream, 10);
        for (var i = 0; i < 9; i++)
        {
            Matrix[i] = BmffFields.I32(stream);
        }

        PreDefined = BmffFields.Bytes(stream, 24);
        NextTrackId = BmffFields.U32(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.Sized(stream, CreationTime, Version == 1 ? 8 : 4);
        BmffFields.Sized(stream, ModificationTime, Version == 1 ? 8 : 4);
        BmffFields.U32(stream, TimeScale);
        BmffFields.Sized(stream, Duration, Version == 1 ? 8 : 4);
        BmffFields.I32(stream, Rate.RawValue);
        BmffFields.I16(stream, Volume.RawValue);
        stream.Write(Reserved);
        foreach (var value in Matrix)
        {
            BmffFields.I32(stream, value);
        }

        stream.Write(PreDefined);
        BmffFields.U32(stream, NextTrackId);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return (Version == 1 ? 112L : 100L) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 1, 0, Offset);
        BmffFields.Width(CreationTime, Version == 1 ? 8 : 4, Offset);
        BmffFields.Width(ModificationTime, Version == 1 ? 8 : 4, Offset);
        BmffFields.Width(Duration, Version == 1 ? 8 : 4, Offset);
        if (TimeScale == 0)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, Offset);
        }

        BmffFields.ArrayLength(Reserved, 10, Offset);
        BmffFields.ArrayLength(Matrix, 9, Offset);
        BmffFields.ArrayLength(PreDefined, 24, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    protected internal override void WriteDescription(TextWriter writer)
    {
        base.WriteDescription(writer);
        writer.Write(System.FormattableString.Invariant($" timescale={TimeScale} duration={Duration} next-track-id={NextTrackId}"));
    }

    private sealed class DebugView
    {
        private readonly MovieHeaderBox _target;

        public DebugView(MovieHeaderBox value)
        {
            _target = value;
        }

        public BmffBoxType BoxType => _target.BoxType;
        public ulong CreationTime => _target.CreationTime;
        public ulong ModificationTime => _target.ModificationTime;
        public uint TimeScale => _target.TimeScale;
        public ulong Duration => _target.Duration;
        public FixedPoint16x16 Rate => _target.Rate;
        public FixedPoint8x8 Volume => _target.Volume;
        public byte[] Reserved => _target.Reserved;
        public int[] Matrix => _target.Matrix;
        public byte[] PreDefined => _target.PreDefined;
        public uint NextTrackId => _target.NextTrackId;
        public byte Version => _target.Version;
        public uint Flags => _target.Flags;
        public bool IsLeaf => _target.IsLeaf;
        public bool IsComposite => _target.IsComposite;
        public long Limit => _target.Limit;
        public long Offset => _target.Offset;
        public global::Assimalign.Cohesion.Content.Bmff.BmffBoxHeader Header => _target.Header;
    }
}
