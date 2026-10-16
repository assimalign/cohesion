using System.Diagnostics;
using System;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

[DebuggerDisplay("Bmff Box: Movie Header (mvhd)")]
/// <summary>The MovieHeaderBox model; field-level support is documented in DESIGN.md.</summary>
public sealed class MovieHeaderBox : BmffFullBox
{
    private static DateTime _seed = new DateTime(1904, 1, 1, 0, 0, 0);

    private DateTime    _headerCreationTime      = DateTime.UtcNow;
    private DateTime    _headerModificationTime  = DateTime.UtcNow;
    private uint        _headerTimeScale;
    private ulong       _headerDuration;
    private double      _headerRate;
    private short       _headerVolume;
    private byte[]      _headerPreDefined = Array.Empty<byte>();
    private uint        _headerNextTrackId;



    /// <summary>Creates a MovieHeaderBox at its original location.</summary>
    /// <param name="size">The original total byte size.</param>
    /// <param name="offset">The absolute original header offset.</param>
    public MovieHeaderBox(long size, long offset)
    {
        base.Version = (byte)BmffVersion.Version1;
        this.Limit = size;
        this.Offset = offset;
        this.Matrix = new int[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 }; // Unity Matrix
    }

    /// <summary>
    /// The version of the movie header.
    /// </summary>
    public new BmffVersion Version
    {
        get => (BmffVersion)base.Version;
        init => base.Version = (byte)value;
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
    public byte[] Reserved { get; set; } = Array.Empty<byte>();
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
    /// <inheritdoc/>
    public override long Limit { get; }

    /// <inheritdoc />
    /// <inheritdoc/>
    public override long Offset { get; }

    /// <inheritdoc />
    /// <inheritdoc/>
    public override bool IsLeaf => true;

    /// <inheritdoc />
    /// <inheritdoc/>
    public override bool IsComposite => false;

    /// <inheritdoc />
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.MovieHeader;


    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
    }

    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        throw new ContentFormatException(ErrorMessages.UnsupportedBox, ContentFormatErrorCode.Unsupported, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
