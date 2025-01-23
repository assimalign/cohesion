using Assimalign.Cohesion.Files.Bmff.Internal;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Files.Bmff;

[DebuggerDisplay("Bmff Box: Track (trak)")]
public sealed class TrackBox : BmffBoxComposite
{
    private IList<BmffBox> children = new List<BmffBox>();

    protected uint _flags;

    private ulong trackCreationTime;
    private ulong trackModificationTime;
    private int trackRate;
    private short trackVolume;

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
        get => (double)trackRate / ((int)ushort.MaxValue + 1);
        set => trackRate = checked((int)Math.Round(value * ((int)short.MaxValue + 1)));
    }

    public double Volume
    {
        get => (double)trackVolume / ((int)byte.MaxValue + 1);
        set => trackVolume = checked((short)Math.Round(value * ((int)byte.MaxValue + 1)));
    }

    public byte[] Reserved { get; private set; }

    public int[] Matrix { get; private set; }

    public byte[] PreDefined { get; private set; }

    public uint NextTrackID { get; set; }

    public override long Limit { get; }

    public override long Offset { get; }

    public override BmffBoxType BoxType => BmffBoxType.Track;

    public override IEnumerable<BmffBox> Children => this.children;

    public override void Read(BmffStream stream)
    {
        var boxes = new List<BmffBox>();
        var reader = new BmffReaderDefault(stream);

        while (reader.Read())
        {
            boxes.Add(reader.Current);
        }

        children = boxes;
    }

    public override void Write(BmffStream stream)
    {

    }
    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
}
