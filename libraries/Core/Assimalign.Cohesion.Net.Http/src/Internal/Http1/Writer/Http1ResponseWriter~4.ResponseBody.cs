using Assimalign.Cohesion.Net.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http1ResponseBodyWriter : Http1ResponseWriter
{
    public Http1ResponseBodyWriter()
    {
        
    }

    public override Task WriteAsync(Http1Context context, ITransportConnection connection)
    {
        return Task.CompletedTask;
    }
}
