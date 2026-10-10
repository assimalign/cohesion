using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// The <c>UseForms()</c> middleware: installs an <see cref="IHttpFormFeature"/> when none is present,
/// parses the form eagerly, and answers a form it cannot read as the client's error instead of letting
/// the parse failure reach the exception boundary.
/// </summary>
/// <remarks>
/// <para>
/// A form over a configured Http.Forms limit is answered <c>413 Content Too Large</c> (RFC 9110
/// §15.5.14), recognized by the <see cref="HttpFormLimitExceededException"/> the parse records as its
/// <see cref="InvalidDataException"/>'s cause; any other <see cref="InvalidDataException"/> is a
/// malformed body, answered <c>400 Bad Request</c>. Both are written as <c>application/problem+json</c>
/// with the payload a source-generated form-bound endpoint writes for the same failure, so a client
/// gets one answer whether this middleware or the endpoint parsed the form. The rest of the pipeline
/// does not run.
/// </para>
/// <para>
/// Nothing else is caught. A body over the transport's own cap, an over-limit decompressed body, a
/// broken connection and a cancelled request belong to their owners, as they do for a form-bound
/// endpoint.
/// </para>
/// </remarks>
internal sealed class FormsMiddleware : IWebApplicationMiddleware
{
    // The payloads match the endpoint-binding generator's form read (EmitFormRead), so the answer to an
    // unreadable form does not depend on which component parsed it.
    private const string tooLargeDetail = "The request form exceeds a configured size limit.";
    private const string malformedDetail = "One or more binding errors occurred.";
    private const string malformedErrorKey = "$form";
    private const string malformedError = "The request form could not be read.";

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        IHttpFeatureCollection features = context.Features;
        IHttpFormFeature? feature = features.Get<IHttpFormFeature>();

        if (feature is null)
        {
            feature = new HttpFormFeature(context.Request);
            features.Set<IHttpFormFeature>(feature);
        }

        // Parse and cache the form. The read stays inline rather than in a helper so a parse that
        // suspends on the body allocates one state machine per request, not two.
        HttpStatusCode? rejection = null;

        try
        {
            await feature.ReadFormAsync(context.RequestCancelled).ConfigureAwait(false);
        }
        catch (InvalidDataException exception) when (exception.InnerException is HttpFormLimitExceededException)
        {
            // The client must send less: RFC 9110 §15.5.14.
            rejection = HttpStatusCode.RequestEntityTooLarge;
        }
        catch (InvalidDataException)
        {
            rejection = HttpStatusCode.BadRequest;
        }

        if (rejection is { } status)
        {
            // Answer the request here; the rest of the pipeline does not run.
            await RejectAsync(context, status).ConfigureAwait(false);
            return;
        }

        await next.Invoke(context).ConfigureAwait(false);
    }

    private static async Task RejectAsync(IHttpContext context, HttpStatusCode status)
    {
        // The parse runs before next, so the head is normally still writable. A middleware ahead of this
        // one may already have committed it, though, and then the status can no longer be set and a
        // problem body would be appended to another response: abort the exchange at the protocol layer
        // instead, so the rest of the pipeline still does not run.
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            await context.CancelAsync().ConfigureAwait(false);
            return;
        }

        ProblemDetails problem;

        if (status == HttpStatusCode.RequestEntityTooLarge)
        {
            problem = ProblemDetails.FromStatus(status, tooLargeDetail);
        }
        else
        {
            problem = ProblemDetails.FromStatus(status, malformedDetail);
            problem.Extensions["errors"] = new Dictionary<string, object?>
            {
                [malformedErrorKey] = new string[] { malformedError }
            };
        }

        await context.Response.WriteProblemDetailsAsync(problem, context.RequestCancelled).ConfigureAwait(false);
    }
}
