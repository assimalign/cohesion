using System;
using static System.Buffers.Binary.BinaryPrimitives;

using Assimalign.Cohesion.Content.Media;
using Assimalign.IO;

namespace Assimalign.Cohesion.Files.Bmff.Internal;

internal sealed partial class BmffReaderDefault : BmffReader
{
    private BmffBox _current;
    private readonly BmffStream _stream;

    public BmffReaderDefault(BmffStream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        this._stream = stream;
    }

    

    public override BmffBox Current => this._current;

    public override bool Read()
    {
        if ((_stream.Position - _stream.Offset) >= _stream.Limit)
        {
            return false;
        }

        // Represents the Offset of the context starting from the beginning of the stream.
        var offset  = _stream.Position;
        var limit   = ReadInt32BigEndian(_stream.ReadBytes(4));
        var type    = ReadInt32BigEndian(_stream.ReadBytes(4));

        if (type < 0 || !Enum.IsDefined(typeof(BmffBoxType), (uint)type))
        {
            // Add Unknown Box
            throw new Exception();
        }
        if ((BmffBoxType)type == BmffBoxType.Meta)
        {

        }
  
        var box = _boxes[(BmffBoxType)type].Invoke(offset, limit);

        // The + and - 8 are to account for the 8 bytes just read above
        box.Read(new BmffStream(_stream, offset + 8, limit - 8)
        {
            BoxType = (BmffBoxType)type
        });

        _current = box;
        _stream.Position = 0;
        _stream.Position = (offset + limit);
        
        return true;
    }

    public override void Dispose()
    {
        _stream.Close();
    }
}
