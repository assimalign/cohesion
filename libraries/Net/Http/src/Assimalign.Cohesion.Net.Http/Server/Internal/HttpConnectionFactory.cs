using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal sealed class HttpConnectionFactory
{
    public static HttpConnectionFactory factory = default!;

    private HttpConnectionFactory() { }

    public HttpConnection Create(HttpConnectionContext context)
    {
        return new Http1Connection(context);
    }


    public static HttpConnectionFactory New() => factory ??= new HttpConnectionFactory();
}
