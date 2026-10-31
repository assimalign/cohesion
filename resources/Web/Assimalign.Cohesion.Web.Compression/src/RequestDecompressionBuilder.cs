using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Compression.Internal;

namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Configures the request-decompression middleware that
/// <c>builder.Services.AddRequestDecompression(decompression =&gt; ...)</c> registers and
/// <see cref="RequestDecompressionExtensions.UseRequestDecompression(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// The decompressed-size guard validates itself on assignment, so <see cref="Build"/> has nothing left
/// to reject. It snapshots the options, so changing them afterwards has no effect on the middleware it
/// returned.
/// </remarks>
public sealed class RequestDecompressionBuilder
{
    /// <summary>
    /// Initializes a builder with the default 100&#8239;MiB decompressed-size guard.
    /// </summary>
    public RequestDecompressionBuilder()
    {
    }

    /// <summary>
    /// Gets the middleware's options: the decompressed-size guard.
    /// </summary>
    public RequestDecompressionOptions Options { get; } = new();

    /// <summary>
    /// Builds the request-decompression middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which decodes gzip, Brotli and deflate request bodies before handlers read them,
    /// answering <c>415</c> for an unsupported coding and <c>413</c> when the decoded body exceeds the
    /// guard.
    /// </returns>
    public IWebApplicationMiddleware Build()
    {
        return new RequestDecompressionMiddleware(Options);
    }
}
