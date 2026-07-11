using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Query.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

/// <summary>
/// Evaluates a conditional QUERY exactly as the equivalent conditional GET (RFC 10008 &#167; 2.6):
/// resolves the target resource's current validators through the registered provider, evaluates
/// the request's precondition fields via the core <see cref="HttpConditionalRequest"/> evaluator,
/// and answers <c>304 Not Modified</c> / <c>412 Precondition Failed</c> without executing the
/// query. On <see cref="HttpPreconditionOutcome.Proceed"/> the validators are stamped onto the
/// response (so clients can condition their next query) and the pipeline continues. Requests with
/// any other method — and queries whose validators the provider does not know — pass through
/// untouched.
/// </summary>
internal sealed class WebQueryConditionalMiddleware : IWebApplicationMiddleware
{
    private readonly WebQueryResourceValidatorsProvider _validatorsProvider;

    public WebQueryConditionalMiddleware(WebQueryResourceValidatorsProvider validatorsProvider)
    {
        _validatorsProvider = validatorsProvider;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        if (context.Request.Method != HttpMethod.Query)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        WebQueryResourceValidators? resolved = await _validatorsProvider.Invoke(context).ConfigureAwait(false);
        if (resolved is not { } validators)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        if (context.TryHandleQueryPreconditions(in validators))
        {
            // 304 / 412 written — the query is not performed (RFC 9110 §13.1.2).
            return;
        }

        HttpContextQueryExtensions.SetValidatorHeaders(context.Response, in validators);
        await next.Invoke(context).ConfigureAwait(false);
    }
}
