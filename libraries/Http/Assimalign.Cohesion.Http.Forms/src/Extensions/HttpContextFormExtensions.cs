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
    private static HttpFormCollection EmptyCollection = new HttpFormCollection();

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
