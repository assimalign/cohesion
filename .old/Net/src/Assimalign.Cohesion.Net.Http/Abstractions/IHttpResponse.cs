using System;
using System.IO;
using System.Net;

namespace Assimalign.Cohesion.Net.Http;

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
    /// 
    /// </summary>
    IHttpCookieCollection Cookies { get; }
    /// <summary>
    /// 
    /// </summary>
    Stream Body { get; }
}
