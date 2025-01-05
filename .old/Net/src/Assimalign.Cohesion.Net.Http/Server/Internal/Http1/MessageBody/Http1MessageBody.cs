using Assimalign.Cohesion.Net.Transports;
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal abstract class Http1MessageBody
{
    protected readonly PipeReader reader;

    public Http1MessageBody(PipeReader reader)
    {
        this.reader = reader;
    }





    public static Http1MessageBody Create(Http1Context context)
    {

        var header = context.Request.Headers;

        if (header.TryGetValue("Transfer-Encoding", out var transferEncoding))
        {
            if (transferEncoding == "chunked")
            {
               // return new Http1RequestChunkEncodingMessageBody();
            }
        }
        else if (header.TryGetValue("Content-Length", out var contentLength))
        {
            if (int.TryParse(contentLength, out var length))
            {
               // return new Http1ContentLengthMessageBody(length);
            }
        }
        else if (header.TryGetValue("Upgrade", out var upgrade))
        {
            //return new Http1UpgradeMessageBody();
        }

        return default;
    }
}
