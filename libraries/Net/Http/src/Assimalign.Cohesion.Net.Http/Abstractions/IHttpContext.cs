using System;

namespace Assimalign.Cohesion.Net.Http;

/// <summary>
/// 
/// </summary>
public interface IHttpContext : IAsyncDisposable
{
    /// <summary>
    /// Represents the HTTP Version being used for the context.
    /// </summary>
    HttpVersion Version { get; }
    /// <summary>
    /// 
    /// </summary>
    IHttpSession? Session { get; }
    /// <summary>
    /// Represents the HTTP Request.
    /// </summary>
    IHttpRequest? Request { get; }
    /// <summary>
    /// Represents the HTTP Response.
    /// </summary>
    /// <remarks>
    /// When creating a custom framework 
    /// Think of the properties within the <see cref="IHttpContext"/> as a reference
    /// that carries the data needs to handle the request and response. 
    /// The actual reading and writing of the response will be done internally.
    /// </remarks>
    IHttpResponse? Response { get; }
    /// <summary>
    /// 
    /// </summary>
    IServiceProvider? ServiceProvider { get; }
}
