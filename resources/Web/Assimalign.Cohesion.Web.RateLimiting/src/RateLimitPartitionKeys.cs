using System;
using System.Net;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// AOT-safe partition-key selectors for <see cref="RateLimitingPolicy"/>. Each returns a stable
/// string key from an exchange, with no reflection; use them inside a partitioner (for example
/// <c>RateLimitPartition.GetFixedWindowLimiter(RateLimitPartitionKeys.ClientAddress(context), ...)</c>)
/// or pass them to <see cref="RateLimitingPolicy.Create{TKey}(Func{IHttpContext, TKey}, Func{TKey, System.Threading.RateLimiting.RateLimiter}, int, System.Collections.Generic.IEqualityComparer{TKey})"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Client-identity keying and BCP 38.</b> Partition on identity a proxy chain <em>vouches for</em>,
/// never on raw, attacker-writable input. <see cref="ClientAddress"/> reads the effective client IP
/// through the forwarded-headers trust model, so behind a trusted proxy it keys on the real client and
/// on a direct connection it keys on the transport peer — an untrusted <c>X-Forwarded-For</c> is not
/// believed. Keying on a spoofable header lets an attacker mint unlimited partitions and defeat the
/// limit, so <see cref="Header"/> is intended for values a trusted gateway injects (an API key, a tenant
/// id), not for reconstructing client identity.
/// </para>
/// </remarks>
public static class RateLimitPartitionKeys
{
    /// <summary>
    /// The key used when a selector cannot resolve a value (for example a connection with no remote IP).
    /// Requests that fall back to it share one partition.
    /// </summary>
    public const string Unresolved = "unresolved";

    /// <summary>
    /// Selects the effective client IP address as the partition key, composing with the forwarded-headers
    /// trust model: the value is <see cref="HttpContextForwardedExtensions.EffectiveRemoteIp"/> — the
    /// client the trusted proxy chain vouches for when the forwarded-headers middleware has run, otherwise
    /// the transport peer address.
    /// </summary>
    /// <param name="context">The HTTP exchange.</param>
    /// <param name="fallback">The key used when no client IP can be resolved. Defaults to <see cref="Unresolved"/>.</param>
    /// <returns>The client IP string, or <paramref name="fallback"/> when none is resolvable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static string ClientAddress(IHttpContext context, string fallback = Unresolved)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPAddress? address = context.EffectiveRemoteIp;
        return address?.ToString() ?? fallback;
    }

    /// <summary>
    /// Selects a request header's value as the partition key. Intended for values a trusted gateway
    /// injects (an API key, a tenant id) — do not use it for client-supplied identity headers, which are
    /// spoofable (see the BCP 38 note on this type).
    /// </summary>
    /// <param name="context">The HTTP exchange.</param>
    /// <param name="key">The header whose value is the partition key.</param>
    /// <param name="fallback">The key used when the header is absent or empty. Defaults to <see cref="Unresolved"/>.</param>
    /// <returns>The header value, or <paramref name="fallback"/> when the header is absent or empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static string Header(IHttpContext context, HttpHeaderKey key, string fallback = Unresolved)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.TryGetValue(key, out HttpHeaderValue value))
        {
            string text = value.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return fallback;
    }
}
