
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;


using Assimalign.Cohesion.Net.Transports;

internal class Http1RequestHttp2PrefaceReader : Http1RequestReader
{
    public Http1RequestHttp2PrefaceReader()
    {
        Next = new Http1RequestLineMethodReader();
    }

    public override Http1RequestReader Next { get; }

    // In unsecure connection it's possible that the 
    public override Task ReadAsync(Http1Context context, ITransportConnection connection)
    {
        return Next.ReadAsync(context, connection);
    }
}
