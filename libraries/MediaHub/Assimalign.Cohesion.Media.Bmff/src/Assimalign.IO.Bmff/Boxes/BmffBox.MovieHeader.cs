using System;
using System.Diagnostics;
using static System.Buffers.Binary.BinaryPrimitives;

namespace Assimalign.IO.Bmff;

[DebuggerDisplay("Bmff Box: Movie Header (mvhd)")]
public sealed class MovieHeaderBox : BmffBox
{
    private static DateTime seed = new DateTime(1904, 1, 1, 0, 0, 0);

    private BmffVersion headerVersion           = BmffVersion.Version1;
    private DateTime    headerCreationTime      = DateTime.UtcNow;
    private DateTime    headerModificationTime  = DateTime.UtcNow;
    private uint        headerTimeScale;
    private ulong       headerDuration;
    private double      headerRate;
    private short       headerVolume;
    private byte[]      headerReserved;
    private byte[]      headerPreDefined;
    private uint        headerNextTrackId;



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
        get => headerVersion;
        init => headerVersion = value;
    }
    /// <summary>
    /// Is an integer that declares the creation time of the presentation (in seconds since midnight, Jan. 1, 1904, in UTC time) 
    /// </summary>
    public DateTime CreationTime 
    {
        get => headerCreationTime;
        init
        {
            if (value < seed)
            {
                throw new InvalidOperationException("");
            }

            headerCreationTime = value;
        }
    }
    /// <summary>
    /// Is an integer that declares the most recent time the presentation
    /// was modified (in seconds since midnight, Jan. 1, 1904, in UTC time) 
    /// </summary>
    public DateTime ModificationTime
    {
        get => headerModificationTime;
        init
        {
            if (value < seed)
            {
                throw new InvalidOperationException("");
            }

            headerModificationTime = value;
        }
    }
    /// <summary>
    /// Is an integer that specifies the time-scale for the entire presentation; this is the number of 
    /// time units that pass in one second.For example, a time coordinate system that measures time in 
    /// sixtieths of a second has a time scale of 60. 
    /// </summary>
    public uint TimeScale
    {
        get => headerTimeScale;
        init => headerTimeScale = value;
    }
    /// <summary>
    /// Is an integer that declares length of the presentation (in the indicated timescale). This
    /// property is derived from the presentation’s tracks: the value of this field corresponds to the duration of
    /// the longest track in the presentation
    /// </summary>
    public ulong Duration
    {
        get => headerDuration;
        init => headerDuration = value;
    }
    /// <summary>
    /// The Movie play rate. Typically 1.0
    /// </summary>
    public double Rate 
    { 
        get => headerRate; 
        init => headerRate = value; 
    }
    /// <summary>
    /// 
    /// </summary>
    public short Volume
    {
        get => headerVolume;
        init => headerVolume = value;
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
        get => this.headerPreDefined;
        init => this.headerPreDefined = value;
    }
    /// <summary>
    /// 
    /// </summary>
    public uint NextTrackId
    {
        get => this.headerNextTrackId;
        init => this.headerNextTrackId = value;
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
        var version = (BmffVersion)ReadInt32BigEndian(stream.ReadBytes(4));

        if (version == BmffVersion.Version1)
        {
            headerVersion = version;
            headerCreationTime = seed + TimeSpan.FromSeconds(unchecked((double)ReadInt64BigEndian(stream.ReadBytes(8))));
            headerModificationTime = seed + TimeSpan.FromSeconds(unchecked((double)ReadInt64BigEndian(stream.ReadBytes(8))));
            headerTimeScale = ReadUInt32BigEndian(stream.ReadBytes(4));
            headerDuration = ReadUInt64BigEndian(stream.ReadBytes(8));
        }
        else
        {
            headerVersion = version;
            headerCreationTime = seed + TimeSpan.FromSeconds(unchecked((double)ReadInt32BigEndian(stream.ReadBytes(4))));
            headerModificationTime = seed + TimeSpan.FromSeconds(unchecked((double)ReadInt32BigEndian(stream.ReadBytes(4))));
            headerTimeScale = ReadUInt32BigEndian(stream.ReadBytes(4));
            headerDuration = ReadUInt32BigEndian(stream.ReadBytes(4));
        }

        headerRate = ReadInt32BigEndian(stream.ReadBytes(4));
        headerVolume = ReadInt16BigEndian(stream.ReadBytes(2));
        headerReserved = stream.ReadBytes(2 + (2 * 4));

        for (int i = 0; i < 9; i++)
        {
            Matrix[i] = ReadInt32BigEndian(stream.ReadBytes(4));
        }

        headerPreDefined = stream.ReadBytes(6 * 4);
        headerNextTrackId = ReadUInt32BigEndian(stream.ReadBytes(4));
    }

    public override void Write(BmffStream stream)
    {
        
    }
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
}