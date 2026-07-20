using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// Builder-time options for the rate-limiting middleware
/// (<see cref="RateLimitingExtensions.UseRateLimiting(IWebApplicationPipelineBuilder, Action{RateLimitingOptions})"/>).
/// Composition is dependency-free: policies, the rejection status, and the hooks are captured at
/// builder time; no service container, configuration binding, or request-time service location occurs.
/// </summary>
public sealed class RateLimitingOptions
{
    // Ordinal (case-sensitive) policy names, matching ASP.NET's rate-limiter policy map: a policy name
    // is a developer-chosen key, so exact-match resolution is the least surprising.
    private readonly Dictionary<string, RateLimitingPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the global limiter policy, applied to every request that reaches the middleware and
    /// acquired up-front with the limiter's full queueing semantics. <see langword="null"/> (the default)
    /// applies no global limit — only endpoints carrying <see cref="RateLimitingMetadata"/> are then
    /// governed. The global limiter and any per-endpoint policy are additive: both must grant a lease.
    /// </summary>
    public RateLimitingPolicy? GlobalPolicy { get; set; }

    /// <summary>
    /// Gets or sets the status written when a request is rejected. Defaults to
    /// <see cref="HttpStatusCode.TooManyRequests"/> (429, RFC 6585 §4).
    /// </summary>
    public HttpStatusCode RejectionStatusCode { get; set; } = HttpStatusCode.TooManyRequests;

    /// <summary>
    /// Gets or sets an optional hook invoked when a request is rejected, after the middleware has set the
    /// status and <c>Retry-After</c> header but before the response is sent. The hook may write its own
    /// response body (or override the status); leaving it unset yields the bodyless default. Not invoked
    /// when the response head has already committed (the exchange is aborted instead).
    /// </summary>
    public Func<RateLimitingRejectionContext, CancellationToken, Task>? OnRejected { get; set; }

    /// <summary>
    /// Gets or sets an optional lightweight observation hook invoked with each limiter decision (the
    /// global limiter, then any per-endpoint policy), for counting admitted/rejected requests without a
    /// hosting-layer metrics dependency. It must not block or throw.
    /// </summary>
    public Action<RateLimitingDecision>? OnDecision { get; set; }

    /// <summary>
    /// Registers a named policy that endpoints reference through <see cref="RateLimitingMetadata"/>.
    /// </summary>
    /// <param name="name">The policy name (compared with ordinal, case-sensitive semantics). Must be unique.</param>
    /// <param name="policy">The policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A policy with the same name is already registered.</exception>
    public RateLimitingOptions AddPolicy(string name, RateLimitingPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(policy);

        if (!_policies.TryAdd(name, policy))
        {
            throw new InvalidOperationException($"A rate limiting policy named '{name}' is already registered.");
        }

        return this;
    }

    /// <summary>
    /// Resolves a named policy registered through <see cref="AddPolicy"/>.
    /// </summary>
    internal bool TryGetPolicy(string name, out RateLimitingPolicy? policy) => _policies.TryGetValue(name, out policy);
}
