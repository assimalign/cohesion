using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the per-exchange request lifetime on <see cref="IHttpContext"/>,
/// backed by an <see cref="IHttpRequestLifetimeFeature"/> stored in the
/// context's feature collection.
/// </summary>
/// <remarks>
/// <para>
/// The protocol core exposes a read-only <see cref="IHttpContext.RequestAborted"/>
/// token. This package adds the <em>writable</em> side: an
/// <see cref="IHttpRequestLifetime"/> whose <c>Abort()</c> can trigger the
/// abort signal and whose <c>RequestAborted</c> token can be observed or
/// replaced. Reading <see cref="RequestLifetime"/> lazily installs a default
/// <see cref="HttpRequestLifetime"/> when none has been attached.
/// </para>
/// <para>
/// Standalone scope: the lazily-installed lifetime is self-contained and is
/// not automatically linked to the transport's
/// <see cref="IHttpContext.RequestAborted"/>. Wiring the two together is a
/// transport concern handled separately.
/// </para>
/// </remarks>
public static class HttpContextRequestLifetimeExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the request lifetime attached to the current exchange,
        /// installing a default <see cref="HttpRequestLifetime"/> on first
        /// read when none is present.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public IHttpRequestLifetime RequestLifetime
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                IHttpRequestLifetimeFeature? feature = context.Features.Get<IHttpRequestLifetimeFeature>();
                if (feature is null)
                {
                    feature = new HttpRequestLifetimeFeature(new HttpRequestLifetime());
                    context.Features.Set<IHttpRequestLifetimeFeature>(feature);
                }

                return feature.RequestLifetime;
            }
        }

        /// <summary>
        /// Aborts the current request, triggering its
        /// <see cref="IHttpRequestLifetime.RequestAborted"/> token. Installs a
        /// default lifetime first when none is present.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public void Abort()
        {
            ArgumentNullException.ThrowIfNull(context);
            context.RequestLifetime.Abort();
        }
    }
}
