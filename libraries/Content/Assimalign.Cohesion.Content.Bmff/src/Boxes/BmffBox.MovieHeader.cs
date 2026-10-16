using System;
using System.Diagnostics;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

[DebuggerDisplay("Bmff Box: Movie Header (mvhd)")]
public sealed class MovieHeaderBox : BmffBox
{
    private static DateTime _seed = new DateTime(1904, 1, 1, 0, 0, 0);

    private BmffVersion _headerVersion           = BmffVersion.Version1;
    private DateTime    _headerCreationTime      = DateTime.UtcNow;
    private DateTime    _headerModificationTime  = DateTime.UtcNow;
    private uint        _headerTimeScale;
    private ulong       _headerDuration;
    private double      _headerRate;
    private short       _headerVolume;
    private byte[]      _headerReserved;
    private byte[]      _headerPreDefined;
    private uint        _headerNextTrackId;



    public MovieHeaderBox(long size, long offset)
    {
        this.Limit = size;
        this.Offset = offset;
        this.Matrix = new int[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 }; // Unity Matrix
    }

    /// <summary>
    /// The version of the movie header.
    /// </summary>
    public BmffVersion Version
    {
        get => _headerVersion;
        init => _headerVersion = value;
    }
    /// <summary>
    /// Is an integer that declares the creation time of the presentation (in seconds since midnight, Jan. 1, 1904, in UTC time) 
    /// </summary>
    public DateTime CreationTime 
    {
        get => _headerCreationTime;
        init
        {
            if (value < _seed)
            {
                throw new InvalidOperationException(ErrorMessages.InvalidHeaderTime);
            }

            _headerCreationTime = value;
        }
    }
    /// <summary>
    /// Is an integer that declares the most recent time the presentation
    /// was modified (in seconds since midnight, Jan. 1, 1904, in UTC time) 
    /// </summary>
    public DateTime ModificationTime
    {
        get => _headerModificationTime;
        init
        {
            if (value < _seed)
            {
                throw new InvalidOperationException(ErrorMessages.InvalidHeaderTime);
            }

            _headerModificationTime = value;
        }
    }
    /// <summary>
    /// Is an integer that specifies the time-scale for the entire presentation; this is the number of 
    /// time units that pass in one second.For example, a time coordinate system that measures time in 
    /// sixtieths of a second has a time scale of 60. 
    /// </summary>
    public uint TimeScale
    {
        get => _headerTimeScale;
        init => _headerTimeScale = value;
    }
    /// <summary>
    /// Is an integer that declares length of the presentation (in the indicated timescale). This
    /// property is derived from the presentation’s tracks: the value of this field corresponds to the duration of
    /// the longest track in the presentation
    /// </summary>
    public ulong Duration
    {
        get => _headerDuration;
        init => _headerDuration = value;
    }
    /// <summary>
    /// The Movie play rate. Typically 1.0
    /// </summary>
    public double Rate 
    { 
        get => _headerRate; 
        init => _headerRate = value; 
    }
    /// <summary>
    /// 
    /// </summary>
    public short Volume
    {
        get => _headerVolume;
        init => _headerVolume = value;
    }
    /// <summary>
    /// 
    /// </summary>
    public byte[] Reserved { get; set; }    
    /// <summary>
    /// 
    /// </summary>
    public int[] Matrix { get; init; }
    /// <summary>
    /// 
    /// </summary>
    public byte[] PreDefined
    {
        get => this._headerPreDefined;
        init => this._headerPreDefined = value;
    }
    /// <summary>
    /// 
    /// </summary>
    public uint NextTrackId
    {
        get => this._headerNextTrackId;
        init => this._headerNextTrackId = value;
    }


    /// <inheritdoc />
    public override long Limit { get; }

    /// <inheritdoc />
    public override long Offset { get; }

    /// <inheritdoc />
    public override bool IsLeaf => true;

    /// <inheritdoc />
    public override bool IsComposite => false;

    /// <inheritdoc />
    public override BmffBoxType BoxType => BmffBoxType.MovieHeader;


    public override void Read(BmffStream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        var version = (BmffVersion)BmffBinary.ReadField(stream, buffer[..4]).ReadInt32BigEndian();

        if (version == BmffVersion.Version1)
        {
            _headerVersion = version;
            _headerCreationTime = _seed + TimeSpan.FromSeconds(unchecked((double)BmffBinary.ReadField(stream, buffer[..8]).ReadInt64BigEndian()));
            _headerModificationTime = _seed + TimeSpan.FromSeconds(unchecked((double)BmffBinary.ReadField(stream, buffer[..8]).ReadInt64BigEndian()));
            _headerTimeScale = BmffBinary.ReadField(stream, buffer[..4]).ReadUInt32BigEndian();
            _headerDuration = BmffBinary.ReadField(stream, buffer[..8]).ReadUInt64BigEndian();
        }
        else
        {
            _headerVersion = version;
            _headerCreationTime = _seed + TimeSpan.FromSeconds(unchecked((double)BmffBinary.ReadField(stream, buffer[..4]).ReadInt32BigEndian()));
            _headerModificationTime = _seed + TimeSpan.FromSeconds(unchecked((double)BmffBinary.ReadField(stream, buffer[..4]).ReadInt32BigEndian()));
            _headerTimeScale = BmffBinary.ReadField(stream, buffer[..4]).ReadUInt32BigEndian();
            _headerDuration = BmffBinary.ReadField(stream, buffer[..4]).ReadUInt32BigEndian();
        }

        // Preserve the legacy public raw-value behavior; field semantics are a Stage 2 repair.
        _headerRate = BmffBinary.ReadField(stream, buffer[..4]).ReadFixedPoint16x16().RawValue;
        _headerVolume = BmffBinary.ReadField(stream, buffer[..2]).ReadFixedPoint8x8().RawValue;
        _headerReserved = new byte[2 + (2 * 4)];
        stream.ReadContentExactly(_headerReserved);

        for (int i = 0; i < 9; i++)
        {
            var field = BmffBinary.ReadField(stream, buffer[..4]);
            Matrix[i] = i % 3 == 2 ? field.ReadFixedPoint2x30().RawValue : field.ReadFixedPoint16x16().RawValue;
        }

        _headerPreDefined = new byte[6 * 4];
        stream.ReadContentExactly(_headerPreDefined);
        _headerNextTrackId = BmffBinary.ReadField(stream, buffer[..4]).ReadUInt32BigEndian();
    }

    public override void Write(BmffStream stream)
    {
        
    }
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
}