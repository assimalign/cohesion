using System;
using System.IO;

namespace Assimalign.Cohesion.Http;

public interface IHttpResponse
{
    /// <summary>
    /// 
    /// </summary>
    HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The collection of Response Headers to be sent back to the client.
    /// </summary>
    IHttpHeaderCollection Headers { get; }

    /// <summary>
    /// Gets the response cookies.
    /// </summary>
    IHttpCookieCollection Cookies { get; }

    /// <summary>
    /// 
    /// </summary>
    Stream Body { get; }
}
