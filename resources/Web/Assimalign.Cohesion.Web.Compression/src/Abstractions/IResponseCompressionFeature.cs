namespace Assimalign.Cohesion.Web.Compression;

using Assimalign.Cohesion.Http;

/// <summary>
/// A per-exchange feature published by the response-compression middleware that lets a handler
/// opt the current response out of compression before it writes the body.
/// </summary>
/// <remarks>
/// <para>
/// The middleware installs this feature at the start of the exchange and reads
/// <see cref="IsEnabled"/> when the response's first body bytes are written &#8212; the point at
/// which it decides whether to compress. A handler that knows its response must not be compressed
/// (for example one already serving a signed or independently encoded representation, or one
/// mixing a secret with reflected input where BREACH is a concern) calls <see cref="Disable"/>
/// while composing the response.
/// </para>
/// <para>
/// Resolve it through the feature collection:
/// <c>context.Features.Get&lt;IResponseCompressionFeature&gt;()?.Disable()</c>. It is present only
/// while the middleware is registered and only until the response starts; disabling after the
/// first body write has no effect, because the compression decision has already been made.
/// </para>
/// </remarks>
public interface IResponseCompressionFeature : IHttpFeature
{
    /// <summary>
    /// Gets a value indicating whether compression is still eligible for the current response.
    /// Starts <see langword="true"/> and becomes <see langword="false"/> once <see cref="Disable"/>
    /// has been called.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Opts the current response out of compression. Idempotent; effective only while the response
    /// has not yet started writing its body.
    /// </summary>
    void Disable();
}
