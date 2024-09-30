using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Claims;

namespace Assimalign.Cohesion.Net.Http.Server.Internal.Http3
{
    internal class Http3Request : IHttpRequest
    {
        public HttpPath Path => throw new NotImplementedException();

        public HttpMethod Method => throw new NotImplementedException();

        public HttpScheme Scheme => HttpScheme.Https;

        public IHttpQueryCollection Query => throw new NotImplementedException();

        public IHttpHeaderCollection Headers => throw new NotImplementedException();

        public IHttpCookieCollection Cookies => throw new NotImplementedException();

        public Stream Body => throw new NotImplementedException();

        public HttpHost Host => throw new NotImplementedException();

        public IHttpFormCollection Form => throw new NotImplementedException();

        public ClaimsPrincipal ClaimsPrincipal => throw new NotImplementedException();
    }
}
