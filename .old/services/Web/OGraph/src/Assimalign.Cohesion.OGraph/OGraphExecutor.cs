
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.OGraph;

using Assimalign.OGraph;
using Assimalign.Cohesion.Net.Http;

public sealed class OGraphExecutor : IOGraphExecutor, IHttpContextExecutor
{
    public OGraphExecutor()
    {
    }


    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
    public Task ExecuteAsync(IOGraphExecutorContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
