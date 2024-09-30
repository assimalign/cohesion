using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal sealed class Http1Context : IHttpContext
{

    internal volatile bool IsDisposed;

    public HttpVersion Version => HttpVersion.Http11;
    public Http1Request Request { get; set; } = new();
    public Http1Response Response { get; set; } = new();
    public Http1Session Session { get; set; } = new();
    public IServiceProvider ServiceProvider { get; set; }

    IHttpSession IHttpContext.Session => this.Session;
    IHttpRequest IHttpContext.Request => this.Request;
    IHttpResponse IHttpContext.Response => this.Response;


    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
