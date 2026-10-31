using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Compression.Internal;

namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Configures the response-compression middleware that
/// <c>builder.Services.AddResponseCompression(compression =&gt; ...)</c> registers and
/// <see cref="ResponseCompressionExtensions.UseResponseCompression(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <see cref="Build"/> validates the codings and compiles the eligible media types once, so an invalid
/// option fails where the application registers compression, never per request. It snapshots the
/// options, so changing them afterwards has no effect on the middleware it returned.
/// </remarks>
public sealed class ResponseCompressionBuilder
{
    /// <summary>
    /// Initializes a builder with the defaults: gzip and Brotli, the seeded text and structured-text
    /// media types, a 1&#8239;KiB threshold, the <c>Fastest</c> level, and HTTPS off (BREACH).
    /// </summary>
    public ResponseCompressionBuilder()
    {
    }

    /// <summary>
    /// Gets the middleware's options: the codings offered, the eligible media types, the size
    /// threshold, the compression level, and the HTTPS opt-in.
    /// </summary>
    public ResponseCompressionOptions Options { get; } = new();

    /// <summary>
    /// Builds the response-compression middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which negotiates gzip or Brotli from each request's <c>Accept-Encoding</c> and
    /// compresses eligible responses, stamping <c>Content-Encoding</c> and <c>Vary: Accept-Encoding</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Both <see cref="ResponseCompressionOptions.EnableGzip"/> and
    /// <see cref="ResponseCompressionOptions.EnableBrotli"/> are disabled, leaving no coding to offer, or
    /// a configured media-type pattern is invalid.
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        return new ResponseCompressionMiddleware(Options);
    }
}
