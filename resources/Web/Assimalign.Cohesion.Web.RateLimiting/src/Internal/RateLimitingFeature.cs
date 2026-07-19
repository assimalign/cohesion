using System;
using System.Collections.Generic;
using System.Threading.RateLimiting;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

/// <summary>
/// The per-exchange <see cref="IRateLimitingFeature"/> implementation. Holds the current limiter
/// decision the request has reached and owns the acquired leases for the exchange: each lease is held
/// (keeping its permit consumed) until the request completes and the middleware disposes the feature,
/// which releases every permit — the correct lifetime for a concurrency limiter whose permit must span
/// the whole request.
/// </summary>
internal sealed class RateLimitingFeature : IRateLimitingFeature, IDisposable
{
    private List<RateLimitLease>? _leases;

    public string Name => nameof(IRateLimitingFeature);

    // Admitted by default: with no limiter governing the request, it is admitted with no policy.
    public string? PolicyName { get; private set; }

    public bool IsAcquired { get; private set; } = true;

    public TimeSpan? RetryAfter { get; private set; }

    /// <summary>Records that a limiter admitted the request under the given policy.</summary>
    public void RecordAdmitted(string? policyName)
    {
        PolicyName = policyName;
        IsAcquired = true;
        RetryAfter = null;
    }

    /// <summary>Records that a limiter rejected the request under the given policy.</summary>
    public void RecordRejected(string? policyName, TimeSpan? retryAfter)
    {
        PolicyName = policyName;
        IsAcquired = false;
        RetryAfter = retryAfter;
    }

    /// <summary>Holds an acquired lease for the request lifetime; released when the feature is disposed.</summary>
    public void TrackLease(RateLimitLease lease) => (_leases ??= new List<RateLimitLease>(2)).Add(lease);

    public void Dispose()
    {
        if (_leases is null)
        {
            return;
        }

        // Release in reverse acquisition order (endpoint before global).
        for (int i = _leases.Count - 1; i >= 0; i--)
        {
            _leases[i].Dispose();
        }

        _leases.Clear();
    }
}
