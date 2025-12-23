using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IHttpRequest
{
    /// <summary>
    /// 
    /// </summary>
    HttpHost Host { get; }

    /// <summary>
    /// 
    /// </summary>
    HttpPath Path { get; }

    /// <summary>
    /// Returns the HTTP Method.
    /// </summary>
    HttpMethod Method { get; }

    /// <summary>
    /// 
    /// </summary>
    HttpScheme Scheme { get; }

    /// <summary>
    /// 
    /// </summary>
    IHttpQueryCollection Query { get; }

    /// <summary>
    /// 
    /// </summary>
    IHttpHeaderCollection Headers { get; }

    /// <summary>
    /// 
    /// </summary>
    IHttpCookieCollection Cookies { get; }

    /// <summary>
    /// 
    /// </summary>
    IHttpFormCollection Form { get; }

    /// <summary>
    /// Gets the body of the request.
    /// </summary>
    Stream Body { get; }

    /// <summary>
    /// Gets the User or Application for this request.
    /// </summary>
    ClaimsPrincipal ClaimsPrincipal { get; }
}
