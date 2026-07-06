using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Per-exchange parsed-form state stored in <see cref="IHttpContext.Features"/>.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core deliberately exposes only the raw request body stream;
/// form-body parsing (<c>application/x-www-form-urlencoded</c>,
/// <c>multipart/form-data</c>) is application code. The
/// <c>Assimalign.Cohesion.Http.Forms</c> package layers parsed-form state on
/// top of the protocol core by attaching this feature to
/// <see cref="IHttpContext.Features"/>. Consumers prefer the
/// <see cref="HttpContextFormExtensions.Form"/> extension property and the
/// <c>ReadFormAsync</c> extension method on <see cref="IHttpContext"/>;
/// middleware that needs a richer feature implementation can install one
/// directly via <c>context.Features.Set&lt;IHttpFormFeature&gt;(...)</c>.
/// </para>
/// <para>
/// <see cref="Form"/> is mutable so the feature acts as a parse cache: a
/// pre-attached collection (set via the <c>Form</c> setter) short-circuits
/// <see cref="ReadFormAsync"/>, and a parsed collection produced by
/// <see cref="ReadFormAsync"/> is stored back into <see cref="Form"/> for
/// subsequent reads.
/// </para>
/// </remarks>
public interface IHttpFormFeature : IHttpFeature
{
    /// <summary>
    /// Gets or sets the parsed form collection. <see langword="null"/> when
    /// the form has not yet been parsed or attached. Assigning a non-null
    /// collection pre-attaches it so <see cref="ReadFormAsync"/> short-circuits
    /// and returns that instance without touching the request body.
    /// </summary>
    IHttpFormCollection? Form { get; set; }

    /// <summary>
    /// Returns the parsed form collection, parsing the request body and
    /// caching the result on first call. Subsequent calls return the cached
    /// collection.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting.</param>
    /// <returns>The parsed form collection.</returns>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default);
}
