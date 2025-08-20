using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http;

public static class HttpContextExtensions
{
    extension(IHttpContext context)
    {
        public void Deconstruct(
            out HttpVersion version,
            out IHttpSession session,
            out IHttpRequest request,
            out IHttpResponse response)
        {
            version = context.Version;
            session = context.Session;
            request = context.Request;
            response = context.Response;
        }
    }
}
