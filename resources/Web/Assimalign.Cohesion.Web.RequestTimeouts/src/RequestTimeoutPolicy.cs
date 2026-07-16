using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// A request-timeout policy: how long a request may execute before it is timed out, and how the
/// timeout is answered on the wire. The global default policy is configured at builder time
/// through <see cref="RequestTimeoutOptions.DefaultPolicy"/>; per-endpoint policies are attached
/// to routes as <see cref="RequestTimeoutMetadata"/> and override the default.
/// </summary>
/// <remarks>
/// <para>
/// A policy with a <see langword="null"/> <see cref="Timeout"/> disables timeout enforcement for
/// the requests it applies to — <see cref="Disabled"/> is the shared instance for that intent
/// (parity with ASP.NET's <c>DisableRequestTimeoutAttribute</c>, expressed as policy data rather
/// than an attribute). The response members are not used by a disabled policy.
/// </para>
/// <para>
/// When a timeout fires and the response has not started, the middleware answers with
/// <see cref="StatusCode"/> (504 by default). Set <see cref="WriteProblemDetails"/> to also write
/// an RFC 9457 <c>application/problem+json</c> payload for the status, or supply
/// <see cref="WriteResponse"/> to own the whole timeout response imperatively —
/// <see cref="WriteResponse"/> wins over both.
/// </para>
/// </remarks>
public sealed class RequestTimeoutPolicy
{
    private readonly TimeSpan? _timeout;

    /// <summary>
    /// A shared policy that disables timeout enforcement (its <see cref="Timeout"/> is
    /// <see langword="null"/>). Attach it to a route via
    /// <see cref="RequestTimeoutMetadata.Disabled"/> to opt an endpoint out of the global default.
    /// </summary>
    public static RequestTimeoutPolicy Disabled { get; } = new();

    /// <summary>
    /// Gets the time a request governed by this policy may execute before it is timed out, or
    /// <see langword="null"/> to disable timeout enforcement.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is set to a non-<see langword="null"/> interval that is zero or negative.
    /// </exception>
    public TimeSpan? Timeout
    {
        get => _timeout;
        init
        {
            if (value is { } timeout && timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    timeout,
                    "The request timeout must be a positive interval. Use a null timeout to disable enforcement.");
            }

            _timeout = value;
        }
    }

    /// <summary>
    /// Gets the status code written when the timeout fires before the response has started.
    /// Defaults to <see cref="HttpStatusCode.GatewayTimeout"/> (504).
    /// </summary>
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Gets whether the timeout response carries an RFC 9457 <c>application/problem+json</c>
    /// payload for <see cref="StatusCode"/> (written through the <c>Web.ProblemDetails</c>
    /// writer). Defaults to <see langword="false"/> — a bare status response, matching ASP.NET's
    /// request-timeouts middleware.
    /// </summary>
    public bool WriteProblemDetails { get; init; }

    /// <summary>
    /// Gets an optional handler that owns the whole timeout response. When set, the middleware
    /// invokes it instead of writing <see cref="StatusCode"/> (and instead of the
    /// <see cref="WriteProblemDetails"/> payload); the handler writes the response imperatively
    /// on the supplied context. It is only invoked when the response has not started.
    /// </summary>
    public Func<IHttpContext, Task>? WriteResponse { get; init; }
}
