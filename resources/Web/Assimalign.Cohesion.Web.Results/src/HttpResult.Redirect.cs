using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A redirection result: sets the <c>Location</c> header (RFC 9110 §10.2.2) and the 3xx status
/// code selected by the <c>permanent</c> × <c>preserveMethod</c> combination.
/// </summary>
/// <remarks>
/// <para>
/// Created through <see cref="Results.Redirect"/> or <see cref="TypedResults.Redirect"/>; the
/// constructor is internal so the factories remain the only entry point. The carrier is immutable
/// and may be reused across exchanges.
/// </para>
/// <para>
/// Status selection: <c>302 Found</c> (neither), <c>301 Moved Permanently</c> (permanent),
/// <c>307 Temporary Redirect</c> (preserve method), <c>308 Permanent Redirect</c> (both). The 301
/// and 302 codes historically allow a user agent to rewrite the method to GET; 307/308 forbid the
/// rewrite (RFC 9110 §15.4).
/// </para>
/// </remarks>
public sealed class RedirectHttpResult : IResult
{
    internal RedirectHttpResult(string url, bool permanent, bool preserveMethod)
    {
        Url = url;
        Permanent = permanent;
        PreserveMethod = preserveMethod;
    }

    /// <summary>
    /// Gets the redirect target written to the <c>Location</c> header — an absolute or relative
    /// URI reference (RFC 3986).
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Gets a value indicating whether the redirect is permanent (301/308 rather than 302/307).
    /// </summary>
    public bool Permanent { get; }

    /// <summary>
    /// Gets a value indicating whether the user agent must preserve the request method when
    /// following the redirect (307/308 rather than 301/302).
    /// </summary>
    public bool PreserveMethod { get; }

    /// <summary>
    /// Gets the status code this result sets, derived from <see cref="Permanent"/> ×
    /// <see cref="PreserveMethod"/>.
    /// </summary>
    public HttpStatusCode StatusCode => (Permanent, PreserveMethod) switch
    {
        (false, false) => HttpStatusCode.Found,
        (true, false) => HttpStatusCode.MovedPermanently,
        (false, true) => HttpStatusCode.RedirectKeepVerb,
        (true, true) => HttpStatusCode.PermanentRedirect,
    };

    /// <summary>
    /// Sets <see cref="StatusCode"/> and the <c>Location</c> header. No body is written.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the response write.</param>
    /// <returns>A task that completes when the status and header have been set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCode;
        context.Response.Headers[HttpHeaderKey.Location] = Url;
        return Task.CompletedTask;
    }
}
