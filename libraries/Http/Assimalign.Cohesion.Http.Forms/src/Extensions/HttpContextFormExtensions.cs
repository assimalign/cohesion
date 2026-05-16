using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the per-exchange parsed <see cref="IHttpFormCollection"/> as a
/// property on <see cref="IHttpRequest"/> and exposes a <c>ReadFormAsync</c>
/// entry point for lazy parsing on <see cref="IHttpContext"/>, both backed
/// by an <see cref="IHttpFormFeature"/> stored in the context's feature
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion HTTP protocol core (<c>Assimalign.Cohesion.Http</c>) deliberately
/// exposes only the raw request body stream &#8211; form-body parsing is an
/// application-layer concern. This package brings property-style access
/// (<c>request.Form</c>) plus an async parse entry point
/// (<c>context.ReadFormAsync()</c>) without forcing the protocol core to depend
/// on a form model.
/// </para>
/// <para>
/// Storage is the strongly-typed <see cref="IHttpContext.Features"/> collection
/// (not the loosely-typed <see cref="IHttpContext.Items"/> dictionary) so the
/// feature can be observed, swapped, or replaced by middleware that captured the
/// reference earlier in the pipeline. The <c>request.Form</c> extension property
/// reaches the feature collection through
/// <see cref="IHttpRequest.HttpContext"/>.
/// </para>
/// </remarks>
public static class HttpContextFormExtensions
{
    extension(IHttpRequest request)
    {
        /// <summary>
        /// Gets or sets the parsed form collection attached to the current
        /// exchange. Returns <see langword="null"/> when no
        /// <see cref="IHttpFormFeature"/> has been installed and
        /// <c>ReadFormAsync</c> has not yet been called.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// On read: <paramref name="request"/> is <see langword="null"/>.
        /// On write: <paramref name="request"/> or the assigned value is
        /// <see langword="null"/>. Setting <see langword="null"/> is rejected
        /// because the property is meant for injecting a pre-parsed collection;
        /// to clear an installed feature, call
        /// <c>request.HttpContext.Features.Set&lt;IHttpFormFeature&gt;(null)</c>.
        /// </exception>
        public IHttpFormCollection? Form
        {
            get
            {
                ArgumentNullException.ThrowIfNull(request);
                return request.HttpContext.Features.Get<IHttpFormFeature>()?.Form;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(value);

                IHttpFormFeature? feature = request.HttpContext.Features.Get<IHttpFormFeature>();
                if (feature is null)
                {
                    request.HttpContext.Features.Set<IHttpFormFeature>(new HttpFormFeature { Form = value });
                }
                else
                {
                    feature.Form = value;
                }
            }
        }
    }

    /// <summary>
    /// Returns the parsed form collection for the current exchange, parsing
    /// the request body and caching the result on first call. Subsequent calls
    /// (and reads through <see cref="Form"/>) return the cached collection.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="cancellationToken">A token to observe while waiting.</param>
    /// <returns>The parsed form collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    /// <remarks>
    /// <para>
    /// PR-1 scaffold: when no <see cref="IHttpFormFeature"/> is installed, an
    /// empty <see cref="HttpFormCollection"/> is produced and cached. A
    /// follow-up PR ports the multipart / urlencoded parser into this package;
    /// until then, callers that already have parsed form data should attach it
    /// via <see cref="Form"/>.
    /// </para>
    /// </remarks>
    public static Task<IHttpFormCollection> ReadFormAsync(
        this IHttpContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IHttpFormFeature? feature = context.Features.Get<IHttpFormFeature>();
        if (feature is null)
        {
            feature = new HttpFormFeature();
            context.Features.Set<IHttpFormFeature>(feature);
        }

        return feature.ReadFormAsync(cancellationToken);
    }
}
