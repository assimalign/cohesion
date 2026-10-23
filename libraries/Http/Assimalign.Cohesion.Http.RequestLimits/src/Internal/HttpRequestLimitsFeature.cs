using System;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Write-through implementation of every per-request limit contract this package provides, on one
/// object: a typed view over the parse context's knobs
/// (<see cref="HttpExchangeInterceptorRequestContext.MaxRequestBodySize"/>,
/// <see cref="HttpExchangeInterceptorRequestContext.MinRequestBodyDataRate"/> and
/// <see cref="HttpExchangeInterceptorRequestContext.MinResponseDataRate"/>). Reads and writes flow to
/// the context, the values the transport enforces, and each read-only lifecycle delegates to the
/// transport-owned freeze flag of its knob.
/// </summary>
/// <remarks>
/// <para>
/// One object, one slot: the feature collection is keyed by <see cref="Name"/>, and a typed lookup
/// returns the first feature implementing the requested contract, so
/// <c>Features.Get&lt;IHttpMaxRequestBodySizeFeature&gt;()</c>,
/// <c>Features.Get&lt;IHttpMinRequestBodyDataRateFeature&gt;()</c> and
/// <c>Features.Get&lt;IHttpMinResponseDataRateFeature&gt;()</c> all find this object. Every exchange
/// pays one allocation and one dictionary slot for all the limits, which keeps the web host's
/// presized feature collection (#1381) the size it was with the body-size feature alone.
/// </para>
/// <para>
/// It registers under the body-size feature's historical name
/// (<see cref="HttpMaxRequestBodySizeFeature.FeatureName"/>), so a name-keyed lookup that found the
/// body-size feature before still finds it.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("HttpRequestLimitsFeature {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class HttpRequestLimitsFeature : IHttpMaxRequestBodySizeFeature, IHttpMinRequestBodyDataRateFeature, IHttpMinResponseDataRateFeature
{
    private readonly HttpExchangeInterceptorRequestContext _context;

    /// <summary>
    /// Initializes the feature as a view over the supplied parse context.
    /// </summary>
    /// <param name="context">The parse context whose knobs this feature projects.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public HttpRequestLimitsFeature(HttpExchangeInterceptorRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public string Name => HttpMaxRequestBodySizeFeature.FeatureName;

    /// <inheritdoc />
    bool IHttpMaxRequestBodySizeFeature.IsReadOnly => _context.IsMaxRequestBodySizeReadOnly;

    /// <inheritdoc />
    long? IHttpMaxRequestBodySizeFeature.MaxRequestBodySize
    {
        get => _context.MaxRequestBodySize;
        // The context setter enforces the contract: InvalidOperationException once the transport has
        // frozen the value, ArgumentOutOfRangeException for negatives.
        set => _context.MaxRequestBodySize = value;
    }

    /// <inheritdoc />
    bool IHttpMinRequestBodyDataRateFeature.IsReadOnly => _context.IsMinRequestBodyDataRateReadOnly;

    /// <inheritdoc />
    HttpMinDataRate? IHttpMinRequestBodyDataRateFeature.MinDataRate
    {
        get => _context.MinRequestBodyDataRate;
        // The context setter throws InvalidOperationException once the transport has frozen the value.
        set => _context.MinRequestBodyDataRate = value;
    }

    /// <inheritdoc />
    bool IHttpMinResponseDataRateFeature.IsReadOnly => _context.IsMinResponseDataRateReadOnly;

    /// <inheritdoc />
    HttpMinDataRate? IHttpMinResponseDataRateFeature.MinDataRate
    {
        get => _context.MinResponseDataRate;
        // The context setter throws InvalidOperationException once the transport has frozen the value.
        set => _context.MinResponseDataRate = value;
    }

    private sealed class DebugView
    {
        private readonly HttpRequestLimitsFeature _target;

        public DebugView(HttpRequestLimitsFeature value)
        {
            _target = value;
        }

        public string Name => _target.Name;
    }
}
