using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.PanopticNet.WebApi.Internal;

using Assimalign.PanopticNet.Http;


internal class WebApiHttpContextExecutor : IHttpContextExecutor
{
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
