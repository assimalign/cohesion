using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal sealed class RequestHookInterceptor : HttpExchangeInterceptor
{
    private readonly Action<HttpExchangeInterceptorRequestContext>? _onHead;

    public RequestHookInterceptor(Action<HttpExchangeInterceptorRequestContext>? onHead = null)
    {
        _onHead = onHead;
    }

    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context)
    {
        _onHead?.Invoke(context);
    }
}
