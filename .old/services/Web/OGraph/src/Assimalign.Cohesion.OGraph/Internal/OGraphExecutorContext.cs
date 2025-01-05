using Assimalign.OGraph;
using Assimalign.Cohesion.Net.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.OGraph.Internal;

internal class OGraphExecutorContext : IOGraphExecutorContext
{
    public IHttpContext HttpContext { get; set; }
    public IOGraphRequest Request => throw new NotImplementedException();
    public IOGraphResponse Response => throw new NotImplementedException();
    public IServiceProvider? ServiceProvider => throw new NotImplementedException();
    public ClaimsPrincipal ClaimsPrincipal => throw new NotImplementedException();
}
