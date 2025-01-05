
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

internal abstract class Http1ResponseWriter
{
    public Http1ResponseWriter Next { get; init; }


    protected bool TryWriteLine(in ReadOnlyMemory<byte> memory)
    {

        return true;
    }


    public abstract Task WriteAsync(Http1Context context, ITransportConnection connection);



    public static Http1ResponseWriter Create()
    {
        return new Http1ResponseLineVersionWriter();
    }
}
