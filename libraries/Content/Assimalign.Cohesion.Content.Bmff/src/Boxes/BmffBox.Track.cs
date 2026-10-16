using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

[DebuggerDisplay("Bmff Box: Track (trak)")]
/// <summary>The TrackBox model; field-level support is documented in DESIGN.md.</summary>
public sealed class TrackBox : BmffBoxComposite
{
    internal override bool SupportsAsyncPlan => true;


    private readonly uint _flags = 0;

    private int _trackRate;
    private short _trackVolume;

    /// <summary>Creates a TrackBox at its original location.</summary>
    /// <param name="size">The original total byte size.</param>
    /// <param name="offset">The absolute original header offset.</param>
    public TrackBox(long size, long offset)
    {
        this.Offset = offset;
        this.Limit = size;
    }

    public byte Version { get; set; }
    public BitArray Flags
    {
        get
        {
            byte[] flagBytes32 = new byte[4];

            BinaryPrimitives.WriteUInt32BigEndian(flagBytes32, _flags);

            byte[] flagBytes24 = new byte[3];
            Buffer.BlockCopy(flagBytes32, 1, flagBytes24, 0, 3);
            return new BitArray(flagBytes24);
        }
    }

    public uint TimeScale { get; set; }
    public ulong Duration { get; set; }



    public double Rate
    {
        get => (double)_trackRate / ((int)ushort.MaxValue + 1);
        set => _trackRate = checked((int)Math.Round(value * ((int)short.MaxValue + 1)));
    }

    public double Volume
    {
        get => (double)_trackVolume / ((int)byte.MaxValue + 1);
        set => _trackVolume = checked((short)Math.Round(value * ((int)byte.MaxValue + 1)));
    }

    public byte[] Reserved { get; private set; } = Array.Empty<byte>();

    public int[] Matrix { get; private set; } = Array.Empty<int>();

    public byte[] PreDefined { get; private set; } = Array.Empty<byte>();

    public uint NextTrackID { get; set; }

    /// <inheritdoc/>
    public override long Limit { get; }

    /// <inheritdoc/>
    public override long Offset { get; }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.Track;



    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        base.Read(stream);
    }

    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        base.Write(stream);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
