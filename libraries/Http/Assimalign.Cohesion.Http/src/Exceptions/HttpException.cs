using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public abstract class HttpException : NetworkException
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    public HttpException(string message) : base(message) { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <param name="inner"></param>
    public HttpException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// 
    /// </summary>
    public HttpErrorCode Code { get; init; }
}
