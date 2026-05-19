using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the per-exchange parsed <see cref="IHttpFormCollection"/> as a
/// property on <see cref="IHttpRequest"/>, backed by an
/// <see cref="IHttpFormFeature"/> stored in the context's feature
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion HTTP protocol core (<c>Assimalign.Cohesion.Http</c>) deliberately
/// exposes only the raw request body stream &#8211; form-body parsing is an
/// application-layer concern. This package brings property-style access
/// (<c>request.Form</c>) without forcing the protocol core to depend on a
/// form model.
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
    private static HttpFormCollection EmptyCollection = new HttpFormCollection();

    extension(IHttpRequest request)
    {
        /// <summary>
        /// Gets the parsed form collection attached to the current exchange.
        /// Returns an empty collection when no <see cref="IHttpFormFeature"/>
        /// has been installed or the installed feature has no parsed body yet.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="request"/> is <see langword="null"/>.
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
        }
    }
}
