using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Server.Internal
{
    internal class DefaultHttpContextExecutor : IHttpContextExecutor
    {
        private readonly HttpContextHandler handler;

        public DefaultHttpContextExecutor(HttpContextHandler handler)
        {
            this.handler = handler;
        }
        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            return handler.Invoke(context, default);
        }
    }
}
