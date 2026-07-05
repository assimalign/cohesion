using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces parsed-form access on top of the HTTP protocol core: a
/// <c>request.Form</c> extension property on <see cref="IHttpRequest"/> and a
/// <c>context.ReadFormAsync(...)</c> extension method on
/// <see cref="IHttpContext"/>, both backed by an <see cref="IHttpFormFeature"/>
/// stored in the context's feature collection.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion HTTP protocol core (<c>Assimalign.Cohesion.Http</c>) deliberately
/// exposes only the raw request body stream &#8211; form-body parsing is an
/// application-layer concern. This package brings property-style access
/// (<c>request.Form</c>) and a lazy parse entry point
/// (<c>context.ReadFormAsync(...)</c>) without forcing the protocol core to
/// depend on a form model.
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
    private static readonly HttpFormCollection EmptyCollection = new HttpFormCollection();

    extension(IHttpRequest request)
    {
        /// <summary>
        /// Gets or sets the parsed form collection attached to the current
        /// exchange.
        /// </summary>
        /// <value>
        /// On get: the collection exposed by the installed
        /// <see cref="IHttpFormFeature"/>, or an empty collection when no feature
        /// has been installed or the installed feature has no parsed body yet.
        /// On set: pre-attaches the supplied collection so subsequent
        /// <see cref="ReadFormAsync(IHttpContext, CancellationToken)"/> calls and
        /// <c>Form</c> reads return that same instance without touching the body
        /// stream, installing an <see cref="IHttpFormFeature"/> if none is
        /// present.
        /// </value>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="request"/> is <see langword="null"/>, or the assigned
        /// value is <see langword="null"/>.
        /// </exception>
        public IHttpFormCollection Form
        {
            get
            {
                ArgumentNullException.ThrowIfNull(request);

                IHttpFormFeature? feature = request.HttpContext.Features.Get<IHttpFormFeature>();

                // If no feature was added then we return an empty collection.
                if (feature is null || feature.Form is null)
                {
                    return EmptyCollection;
                }
                return feature.Form;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(value);

                IHttpFeatureCollection features = request.HttpContext.Features;
                IHttpFormFeature? feature = features.Get<IHttpFormFeature>();
                if (feature is null)
                {
                    features.Set<IHttpFormFeature>(new HttpFormFeature(value));
                }
                else
                {
                    feature.Form = value;
                }
            }
        }
    }

    extension(IHttpContext context)
    {
        /// <summary>
        /// Returns the parsed form collection for the current exchange,
        /// installing a default <see cref="HttpFormFeature"/> over
        /// <see cref="IHttpContext.Request"/> when no feature is present,
        /// triggering the lazy parse, and caching the result. Subsequent calls
        /// return the same cached collection.
        /// </summary>
        /// <param name="cancellationToken">A token to observe while waiting.</param>
        /// <returns>The parsed form collection.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="context"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was cancelled.
        /// </exception>
        /// <exception cref="System.IO.InvalidDataException">
        /// The body violates one of the configured <see cref="HttpFormOptions"/>
        /// limits.
        /// </exception>
        public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            IHttpFormFeature? feature = context.Features.Get<IHttpFormFeature>();
            if (feature is null)
            {
                feature = new HttpFormFeature(context.Request);
                context.Features.Set<IHttpFormFeature>(feature);
            }

            return feature.ReadFormAsync(cancellationToken);
        }
    }
}
