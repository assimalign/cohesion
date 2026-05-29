using System;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Transport-security helpers layered over <see cref="ITransportConnectionContext.Items"/>.
/// </summary>
/// <remarks>
/// <para>
/// Provides a typed surface for the transport-level <c>IsSecure</c>
/// signal so transport middleware that establishes a secure session
/// (TLS over TCP via <see cref="System.Net.Security.SslStream"/>,
/// mutual-TLS proxies, peer-authentication adapters, etc.) can flag
/// the connection without needing a shared interface contract, and
/// upper layers can read it without parsing string keys out of
/// <see cref="ITransportConnectionContext.Items"/>.
/// </para>
/// <para>
/// The underlying storage is the existing
/// <see cref="ITransportConnectionContext.Items"/> dictionary keyed by
/// <see cref="IsSecureItemKey"/>; the helper does the casting and
/// missing-key handling so consumers do not have to. The
/// <c>get</c> returns <see langword="false"/> when no value has been
/// recorded.
/// </para>
/// <para>
/// Middleware that wraps the connection's underlying stream in a
/// secure adapter is expected to set this <em>after</em> the secure
/// handshake completes successfully:
/// </para>
/// <code language="csharp">
/// options.Use(async (context, next) =&gt;
/// {
///     SslStream ssl = new(context.Pipe.GetStream(), leaveInnerStreamOpen: false);
///     await ssl.AuthenticateAsServerAsync(/* ... */);
///     context.IsSecure = true;
///     await next();
/// });
/// </code>
/// </remarks>
public static class TransportSecurityExtensions
{
    /// <summary>
    /// The <see cref="ITransportConnectionContext.Items"/> key under
    /// which the secure flag is stored. The fully-qualified form avoids
    /// collisions with consumer-supplied keys.
    /// </summary>
    public const string IsSecureItemKey = "Assimalign.Cohesion.Transports.IsSecure";

    extension(ITransportConnectionContext context)
    {
        /// <summary>
        /// Gets or sets whether the underlying transport connection is
        /// currently secured (for example, by a TLS handshake performed
        /// by an earlier middleware in the connection pipeline).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The setter writes through to
        /// <see cref="ITransportConnectionContext.Items"/> under
        /// <see cref="IsSecureItemKey"/>. The getter returns
        /// <see langword="false"/> when no value has been recorded (or
        /// when a value of a non-<see cref="bool"/> type was stored
        /// under the key by a buggy consumer).
        /// </para>
        /// <para>
        /// Once flipped to <see langword="true"/> this is expected to
        /// stay true for the lifetime of the connection &#8212;
        /// transport-level secure adapters do not gracefully
        /// down-negotiate within a single connection. Mid-connection
        /// renegotiation that fails should abort the connection rather
        /// than reset this flag.
        /// </para>
        /// </remarks>
        public bool IsSecure
        {
            get
            {
                ArgumentNullException.ThrowIfNull(context);

                return context.Items.TryGetValue(IsSecureItemKey, out object? value)
                    && value is bool secure
                    && secure;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(context);

                context.Items[IsSecureItemKey] = value;
            }
        }
    }
}
