using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>Uses the shared buffered/streamed transport head hook without tracking another start.</summary>
internal sealed class WebResponseStartingInterceptor : HttpExchangeInterceptor
{
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

    public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
    {
        context.Features.Set(new ResponseStartingFeature(context));
    }

    public override ValueTask BeforeResponseHeadAsync(
        HttpExchangeInterceptorResponseContext context,
        CancellationToken cancellationToken = default)
    {
        return context.Features.Get<ResponseStartingFeature>()?.InvokeAsync(cancellationToken)
            ?? ValueTask.CompletedTask;
    }
}
