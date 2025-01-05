using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http1Response : IHttpResponse
{
    public HttpStatusCode StatusCode { get; set; } = 404;
    public IHttpHeaderCollection Headers { get;  } = new HttpHeaderCollection();
    public IHttpCookieCollection Cookies => throw new NotImplementedException();
    public Stream Body { get; set; }
}
