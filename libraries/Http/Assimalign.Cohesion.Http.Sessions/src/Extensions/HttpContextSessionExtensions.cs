using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Surfaces the per-exchange <see cref="IHttpSession"/> on
/// <see cref="IHttpContext"/> as a property, backed by an
/// <see cref="IHttpSessionFeature"/> stored in the context's feature collection.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion HTTP protocol core (<c>Assimalign.Cohesion.Http</c>) deliberately
/// omits sessions &#8211; they are an application-layer concept, not part of the
/// wire protocol. This package brings property-style access (<c>context.Session</c>,
/// <c>context.RequireSession</c>) without forcing the protocol core to depend on
/// the session model.
/// </para>
/// <para>
/// Storage is the strongly-typed <see cref="IHttpContext.Features"/> collection
/// (not the loosely-typed <see cref="IHttpContext.Items"/> dictionary) so the
/// feature can be observed, swapped, or replaced by middleware that captured the
/// reference earlier in the pipeline.
/// </para>
/// </remarks>
public static class HttpContextSessionExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets or sets the session attached to the current exchange. Returns
        /// <see langword="null"/> when no session middleware has installed an
        /// <see cref="IHttpSessionFeature"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// On read: <paramref name="context"/> is <see langword="null"/>.
        /// On write: <paramref name="context"/> or the assigned value is
        /// <see langword="null"/>. Setting <see langword="null"/> is rejected
        /// because the feature invariant is "if installed, <see cref="IHttpSessionFeature.Session"/>
        /// is non-null." To remove an installed session, call
        /// <c>context.Features.Set&lt;IHttpSessionFeature&gt;(null)</c>.
        /// </exception>
        public IHttpSession? Session
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpSessionFeature>()?.Session;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(context);
                ArgumentNullException.ThrowIfNull(value);

                IHttpSessionFeature? feature = context.Features.Get<IHttpSessionFeature>();
                if (feature is null)
                {
                    context.Features.Set<IHttpSessionFeature>(new HttpSessionFeature(value));
                }
                else
                {
                    feature.Session = value;
                }
            }
        }

        /// <summary>
        /// Gets the session attached to the current exchange, throwing when none
        /// has been installed. Use when the caller treats the session as a
        /// required dependency.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// No <see cref="IHttpSessionFeature"/> has been attached. Assign
        /// <see cref="Session"/> before requiring it, or install a feature
        /// directly through <see cref="IHttpContext.Features"/>.
        /// </exception>
        public IHttpSession RequireSession
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);
                return context.Features.Get<IHttpSessionFeature>()?.Session
                    ?? throw new InvalidOperationException(
                        "No IHttpSessionFeature has been attached to the HTTP context. Assign Session before requiring it.");
            }
        }
    }
}
