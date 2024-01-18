using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

public partial class HttpServerOptions
{
    internal IHttpContextExecutor Executor;

    public void UseExecutor(IHttpContextExecutor executor)
    {
        if (executor is null)
        {
            throw new ArgumentNullException(nameof(executor));
        }
    }
}
