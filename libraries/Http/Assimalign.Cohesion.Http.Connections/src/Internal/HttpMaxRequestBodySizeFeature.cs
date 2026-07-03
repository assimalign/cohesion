using System;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Transport implementation of <see cref="IHttpMaxRequestBodySizeFeature"/>. Seeded with the
/// connection-wide default cap and made read-only by the transport once the request body has
/// been read.
/// </summary>
internal sealed class HttpMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
{
    /// <summary>
    /// The name this feature registers under in the exchange's
    /// <see cref="IHttpFeatureCollection"/>.
    /// </summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.MaxRequestBodySize";

    private long? _maxRequestBodySize;

    /// <summary>
    /// Initializes the feature with the effective per-request cap.
    /// </summary>
    /// <param name="maxRequestBodySize">The initial cap in octets, or <see langword="null"/> for unbounded.</param>
    public HttpMaxRequestBodySizeFeature(long? maxRequestBodySize)
    {
        _maxRequestBodySize = maxRequestBodySize;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public bool IsReadOnly { get; private set; }

    /// <inheritdoc />
    public long? MaxRequestBodySize
    {
        get => _maxRequestBodySize;
        set
        {
            if (IsReadOnly)
            {
                throw new InvalidOperationException(
                    "The maximum request body size cannot be modified after the request body has started to be read.");
            }

            if (value is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The maximum request body size must be non-negative or null (unbounded).");
            }

            _maxRequestBodySize = value;
        }
    }

    /// <summary>
    /// Freezes the effective cap. Called by the transport before the request body is read so that
    /// subsequent attempts to change the cap for this exchange fail per the feature contract.
    /// </summary>
    public void MakeReadOnly()
    {
        IsReadOnly = true;
    }
}
