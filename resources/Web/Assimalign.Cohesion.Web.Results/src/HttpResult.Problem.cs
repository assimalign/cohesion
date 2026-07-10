using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// A machine-readable error result: writes a <see cref="ProblemDetails"/> payload as
/// <c>application/problem+json</c> (RFC 9457) using the AOT-safe hand-rolled writer.
/// </summary>
/// <remarks>
/// <para>
/// Created through <see cref="Results.Problem(ProblemDetails)"/> or
/// <see cref="TypedResults.Problem(ProblemDetails)"/>; the constructor is internal so the factories
/// remain the only entry point.
/// </para>
/// <para>
/// Construction normalizes the supplied <see cref="ProblemDetails"/> in place:
/// <see cref="ProblemDetails.Status"/> defaults to <c>500</c>, and when the problem uses the
/// reserved default type (<c>about:blank</c>), a missing <see cref="ProblemDetails.Title"/> is
/// filled with the status phrase per RFC 9457 §4.2. Serialization always goes through
/// <see cref="ProblemDetailsWriter.Default"/> — there is exactly one problem+json serializer in
/// the framework.
/// </para>
/// </remarks>
public sealed class ProblemHttpResult : IResult
{
    internal ProblemHttpResult(ProblemDetails problemDetails)
    {
        problemDetails.Status ??= HttpStatusCode.InternalServerError.Value;

        if (problemDetails.Title is null &&
            (problemDetails.Type is null || problemDetails.Type == ProblemDetailsDefaults.DefaultType))
        {
            problemDetails.Title = ProblemDetailsDefaults.GetTitle(problemDetails.Status.Value);
        }

        ProblemDetails = problemDetails;
    }

    /// <summary>
    /// Gets the problem payload this result writes. Normalized at construction: a status code is
    /// always present.
    /// </summary>
    public ProblemDetails ProblemDetails { get; }

    /// <summary>
    /// Gets the <c>Content-Type</c> this result sets: <c>application/problem+json</c>.
    /// </summary>
    public string ContentType => ProblemDetailsDefaults.MediaType;

    /// <summary>
    /// Sets the status code from <see cref="ProblemDetails"/>, serializes the payload with the
    /// AOT-safe default writer, and writes it as <c>application/problem+json</c> with
    /// <c>Content-Length</c>.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the body write.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        byte[] payload = ProblemDetailsJsonWriter.Instance.WriteToUtf8Bytes(ProblemDetails);

        return HttpResultWriter.WritePayloadAsync(
            context,
            new HttpStatusCode(ProblemDetails.Status!.Value),
            ProblemDetailsDefaults.MediaType,
            payload,
            cancellationToken);
    }
}
