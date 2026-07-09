using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Write-through <see cref="IHttpMaxRequestBodySizeFeature"/> implementation: a typed view over
/// the parse context's <see cref="HttpExchangeInterceptorRequestContext.MaxRequestBodySize"/> knob.
/// Reads and writes flow to the context — the value the transport actually enforces — and the
/// read-only lifecycle delegates to the transport-owned freeze flag, so the feature's contract
/// survives the transport changing <em>when</em> it freezes (buffered materialization today,
/// first body byte once bodies stream).
/// </summary>
internal sealed class HttpMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
{
    /// <summary>
    /// The name this feature registers under in the exchange's feature collection. Kept
    /// identical to the historical transport-seeded value so name-keyed lookups are stable.
    /// </summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.MaxRequestBodySize";

    private readonly HttpExchangeInterceptorRequestContext _context;

    /// <summary>
    /// Initializes the feature as a view over the supplied parse context.
    /// </summary>
    /// <param name="context">The parse context whose body-size knob this feature projects.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public HttpMaxRequestBodySizeFeature(HttpExchangeInterceptorRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public bool IsReadOnly => _context.IsMaxRequestBodySizeReadOnly;

    /// <inheritdoc />
    public long? MaxRequestBodySize
    {
        get => _context.MaxRequestBodySize;
        // The context setter enforces the full contract: InvalidOperationException once the
        // transport has frozen the value, ArgumentOutOfRangeException for negatives.
        set => _context.MaxRequestBodySize = value;
    }
}
