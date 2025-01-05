using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Net.Http;

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
    /// 
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
    /// 
    /// </summary>
    Stream Body { get; }
    /// <summary>
    /// Gets the User Or Application for this request.
    /// </summary>
    ClaimsPrincipal ClaimsPrincipal { get; }
}
