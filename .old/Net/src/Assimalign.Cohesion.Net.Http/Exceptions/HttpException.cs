using System;

namespace Assimalign.Cohesion.Net.Http;

public abstract class HttpException : Exception
{
    public HttpException(string message) : base(message) { }
    public HttpException(string message, Exception inner) : base(message, inner) { }


    public HttpExceptionCode Code { get; init; }
}
