using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http1ChuckedEncodingMessageBody : Http1MessageBody
{
    private enum Mode
    {
        Prefix,
        Extension,
        Data,
        Suffix,
        Trailer,
        TrailerHeaders,
        Complete
    };

    public Http1ChuckedEncodingMessageBody(PipeReader reader) : base(reader)
    {
       
    }


    private Mode mode;



    private static int GetChunkSize(int extraHexDigit, int currentParsedSize)
    {
        try
        {
            checked
            {
                if (extraHexDigit >= '0' && extraHexDigit <= '9')
                {
                    return currentParsedSize * 0x10 + (extraHexDigit - '0');
                }
                else if (extraHexDigit >= 'A' && extraHexDigit <= 'F')
                {
                    return currentParsedSize * 0x10 + (extraHexDigit - ('A' - 10));
                }
                else if (extraHexDigit >= 'a' && extraHexDigit <= 'f')
                {
                    return currentParsedSize * 0x10 + (extraHexDigit - ('a' - 10));
                }
            }
        }
        catch (OverflowException ex)
        {
            //throw new IOException(CoreStrings.BadRequest_BadChunkSizeData, ex);
        }

        //KestrelBadHttpRequestException.Throw(RequestRejectionReason.BadChunkSizeData);

        return -1; // can't happen, but compiler complains
    }
}


internal class Test : PipeReader
{
    public override void AdvanceTo(SequencePosition consumed)
    {
        throw new NotImplementedException();
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        throw new NotImplementedException();
    }

    public override void CancelPendingRead()
    {
        throw new NotImplementedException();
    }

    public override void Complete(Exception? exception = null)
    {
        throw new NotImplementedException();
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public override bool TryRead(out ReadResult result)
    {
        throw new NotImplementedException();
    }
}