using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

internal class Http3Connection : HttpConnection
{


    //protected override IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    //{
    //    throw new NotImplementedException();
    //}

    //protected override Task<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    //{
    //    throw new NotImplementedException();
    //}
    public override void Dispose()
    {
        throw new NotImplementedException();
    }

    public override IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public override IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
