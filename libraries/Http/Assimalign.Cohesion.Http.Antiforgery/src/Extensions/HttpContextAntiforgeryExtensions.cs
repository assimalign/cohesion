using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the antiforgery service for the current exchange on
/// <see cref="IHttpContext"/>, backed by an
/// <see cref="IHttpAntiforgeryFeature"/> stored in the context's feature
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// The antiforgery service is stateless and created once per application via
/// <see cref="HttpAntiforgery.Create(System.Action{HttpAntiforgeryOptions})"/>.
/// Assigning it through <see cref="Antiforgery"/> installs a feature so
/// downstream handlers can resolve the same configured service without
/// re-creating it (and therefore validate against the same signing key).
/// </para>
/// </remarks>
public static class HttpContextAntiforgeryExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets or sets the antiforgery service attached to the current
        /// exchange. Returns <see langword="null"/> when no
        /// <see cref="IHttpAntiforgeryFeature"/> has been installed.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// On read: <paramref name="context"/> is <see langword="null"/>.
        /// On write: <paramref name="context"/> or the assigned value is
        /// <see langword="null"/>.
        /// </exception>
        public IHttpAntiforgery? Antiforgery
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpAntiforgeryFeature>()?.Antiforgery;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(context);
                ArgumentNullException.ThrowIfNull(value);
                context.Features.Set<IHttpAntiforgeryFeature>(new HttpAntiforgeryFeature(value));
            }
        }

        /// <summary>
        /// Gets the antiforgery service attached to the current exchange,
        /// throwing when none has been installed.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// No <see cref="IHttpAntiforgeryFeature"/> has been attached. Assign
        /// <see cref="Antiforgery"/> before requiring it.
        /// </exception>
        public IHttpAntiforgery RequireAntiforgery
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpAntiforgeryFeature>()?.Antiforgery
                    ?? throw new InvalidOperationException(
                        "No IHttpAntiforgeryFeature has been attached to the HTTP context. Assign Antiforgery before requiring it.");
            }
        }
    }
}
