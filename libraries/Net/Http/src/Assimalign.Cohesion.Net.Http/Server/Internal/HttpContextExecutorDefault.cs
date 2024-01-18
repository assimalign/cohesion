using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class HttpContextExecutor : IHttpContextExecutor
{
    public virtual Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        context.Response.Headers.Add("Content-Length", "0");
        context.Response.Headers.Add("Server", "Cohesion.Net");
        return Task.CompletedTask;
    }
}