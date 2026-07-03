namespace Assimalign.Cohesion.Http;

/// <summary>
/// A typed <see cref="IHttpFeature"/> exposing, and allowing per-request adjustment of, the
/// maximum request body size the transport will accept for the current exchange.
/// </summary>
/// <remarks>
/// <para>
/// The transport seeds this feature on every request with the connection-wide default
/// (the listener's configured maximum request body size). An endpoint or middleware can read
/// <see cref="MaxRequestBodySize"/> to discover the effective cap, or assign it &#8212; to raise
/// the cap for an endpoint that legitimately accepts large uploads, or lower it for one that
/// must not &#8212; provided the request body has not yet begun to be read
/// (<see cref="IsReadOnly"/> is <see langword="false"/>).
/// </para>
/// <para>
/// A value of <see langword="null"/> means the body size is unbounded for this request. Once the
/// transport begins reading the request body it makes the feature read-only, after which assigning
/// <see cref="MaxRequestBodySize"/> throws; this mirrors Kestrel's
/// <c>IHttpMaxRequestBodySizeFeature</c> contract so callers can rely on the same lifecycle.
/// </para>
/// </remarks>
public interface IHttpMaxRequestBodySizeFeature : IHttpFeature
{
    /// <summary>
    /// Gets a value indicating whether <see cref="MaxRequestBodySize"/> can still be changed.
    /// Returns <see langword="true"/> once the transport has begun reading the request body,
    /// after which the effective cap is fixed for the remainder of the exchange.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets or sets the maximum request body size, in octets, accepted for this exchange, or
    /// <see langword="null"/> to leave it unbounded.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when set after the request body has started to be read (<see cref="IsReadOnly"/> is
    /// <see langword="true"/>).
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// Thrown when set to a negative value.
    /// </exception>
    long? MaxRequestBodySize { get; set; }
}
