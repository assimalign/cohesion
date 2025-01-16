using System;
using static System.Buffers.Binary.BinaryPrimitives;

namespace Assimalign.IO.Bmff.Internal;

internal sealed partial class BmffReaderDefault : BmffReader
{
    private BmffBox current;
    private readonly BmffStream stream;

    public BmffReaderDefault(BmffStream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        this.stream = stream;
    }

    

    public override BmffBox Current => this.current;

    public override bool Read()
    {
        if ((stream.Position - stream.Offset) >= stream.Limit)
        {
            return false;
        }

        // Represents the Offset of the context starting from the beginning of the stream.
        var offset  = stream.Position;
        var limit   = ReadInt32BigEndian(stream.ReadBytes(4));
        var type    = ReadInt32BigEndian(stream.ReadBytes(4));

        if (type < 0 || !Enum.IsDefined(typeof(BmffBoxType), (uint)type))
        {
            // Add Unknown Box
            throw new Exception();
        }
        if ((BmffBoxType)type == BmffBoxType.Meta)
        {

        }
  
        var box = boxes[(BmffBoxType)type].Invoke(offset, limit);

        // The + and - 8 are to account for the 8 bytes just read above
        box.Read(new BmffStream(stream, offset + 8, limit - 8)
        {
            BoxType = (BmffBoxType)type
        });

        current = box;
        stream.Position = 0;
        stream.Position = (offset + limit);
        
        return true;
    }

    public override void Dispose()
    {
        stream.Close();
    }
}
