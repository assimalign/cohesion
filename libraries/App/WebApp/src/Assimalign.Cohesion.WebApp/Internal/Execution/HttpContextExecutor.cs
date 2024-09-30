using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.WebApp.Internal;

using Assimalign.Cohesion.Net.Http;

internal class HttpContextExecutor : IHttpContextExecutor
{
    public HttpContextExecutor()
    {

    }

    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
