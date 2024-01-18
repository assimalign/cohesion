using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http3Context : IHttpContext
{
    public HttpVersion Version => throw new NotImplementedException();

    public IHttpSession? Session => throw new NotImplementedException();

    public IHttpRequest? Request => throw new NotImplementedException();

    public IHttpResponse? Response => throw new NotImplementedException();

    public IServiceProvider? ServiceProvider => throw new NotImplementedException();

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }
}
