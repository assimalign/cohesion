using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal abstract class HttpContext : IHttpContext
{
    public abstract HttpVersion Version { get; }
    public abstract IHttpSession? Session { get; }
    public abstract IHttpRequest? Request { get; }
    public abstract IHttpResponse? Response { get; }
    public abstract IServiceProvider? ServiceProvider { get; }
    public abstract ValueTask DisposeAsync();
}