using System;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// The per-exchange timeout engine and the <see cref="IHttpRequestTimeoutFeature"/>
/// implementation. Owns two cancellation sources: the timeout source (armed and re-armed against
/// the composed <see cref="TimeProvider"/>) and a source linking it with the transport's
/// <see cref="IHttpContext.RequestCancelled"/> — the linked token is what downstream work
/// observes, so it trips on expiry <em>and</em> on a genuine request cancellation.
/// </summary>
/// <remarks>
/// The timeout source is created unarmed (an infinite due time) so a per-endpoint policy or a
/// handler's <see cref="SetTimeout"/> can arm it even when no global default exists. Re-arming
/// after the source has fired is inherently a no-op (<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
/// cannot un-cancel), which is exactly the documented race semantic of <see cref="Disable"/>.
/// </remarks>
internal sealed class HttpRequestTimeoutFeature : IHttpRequestTimeoutFeature, IDisposable
{
    private readonly CancellationTokenSource _timeoutSource;
    private readonly CancellationTokenSource _linkedSource;
    private RequestTimeoutPolicy? _policy;

    public HttpRequestTimeoutFeature(IHttpContext context, RequestTimeoutOptions options)
    {
        _timeoutSource = new CancellationTokenSource(Timeout.InfiniteTimeSpan, options.TimeProvider);
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(context.RequestCancelled, _timeoutSource.Token);
        _policy = options.DefaultPolicy;

        if (_policy?.Timeout is { } timeout)
        {
            _timeoutSource.CancelAfter(timeout);
        }
    }

    public string Name => nameof(IHttpRequestTimeoutFeature);

    public CancellationToken Token => _linkedSource.Token;

    /// <summary>
    /// The policy in effect for the exchange: the endpoint policy once one has been applied,
    /// otherwise the global default; <see langword="null"/> when neither exists.
    /// </summary>
    public RequestTimeoutPolicy? EffectivePolicy => _policy;

    /// <summary>
    /// Whether the timeout timer has fired. Distinguishes an expiry from a genuine request
    /// cancellation when both surface as an <see cref="OperationCanceledException"/> — the
    /// middleware additionally checks that <see cref="IHttpContext.RequestCancelled"/> itself is
    /// not cancelled before attributing the unwind to the timeout.
    /// </summary>
    public bool TimedOut => _timeoutSource.IsCancellationRequested;

    public void Disable() => _timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);

    public void SetTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The request timeout must be a positive interval. Use Disable() to turn enforcement off.");
        }

        _timeoutSource.CancelAfter(timeout);
    }

    /// <summary>
    /// Applies the matched endpoint's policy, replacing the global default: re-arms the timer to
    /// the endpoint's interval (measured from the match) or disarms it for a disabled policy.
    /// Invoked by the middleware's feature-collection decorator at the moment the router publishes
    /// the route match — before the endpoint's handler runs.
    /// </summary>
    /// <param name="metadata">The endpoint's timeout metadata.</param>
    public void ApplyEndpointPolicy(RequestTimeoutMetadata metadata)
    {
        _policy = metadata.Policy;

        if (metadata.Policy.Timeout is { } timeout)
        {
            _timeoutSource.CancelAfter(timeout);
        }
        else
        {
            _timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _linkedSource.Dispose();
        _timeoutSource.Dispose();
    }
}
