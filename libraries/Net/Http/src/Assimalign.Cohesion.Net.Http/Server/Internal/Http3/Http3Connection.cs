using Assimalign.Cohesion.Net.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http3Connection : HttpConnection
{
    internal override IAsyncEnumerable<IHttpContext> ProcessAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
    
    //protected override IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    //{
    //    throw new NotImplementedException();
    //}

    //protected override Task<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    //{
    //    throw new NotImplementedException();
    //}

    
}
