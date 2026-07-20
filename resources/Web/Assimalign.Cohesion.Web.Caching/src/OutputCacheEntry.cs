using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// An immutable stored output-cache record: either a cached response representation (status, headers,
/// and body) or a lightweight <em>vary marker</em> that records which request headers a primary cache
/// key's representations depend on. The <see cref="IOutputCacheStore"/> treats the entry as an opaque
/// payload — it never interprets the body or headers, only stores and returns it under a key with the
/// entry's own tags and time-to-live.
/// </summary>
/// <remarks>
/// <para>
/// <b>Representations vs. markers.</b> When <see cref="Body"/> is non-<see langword="null"/> the entry
/// is a stored response. When <see cref="Body"/> is <see langword="null"/> and <see cref="VaryBy"/> is
/// non-empty the entry is a vary marker: the RFC 9111 §4.1 indirection that lets a lookup discover, for
/// a primary key, which request headers the stored variants key on before it computes the secondary
/// (variant) key. <see cref="IsVaryMarker"/> reports which shape the entry has.
/// </para>
/// <para>
/// <b>AOT posture.</b> The record carries only value types, strings, and a <see cref="byte"/> array, so
/// the in-memory store holds it directly with no serialization and a distributed adapter can frame it
/// without reflection.
/// </para>
/// </remarks>
public sealed class OutputCacheEntry
{
    /// <summary>
    /// Initializes a new stored entry.
    /// </summary>
    /// <param name="statusCode">The response status code (unused for a vary marker).</param>
    /// <param name="headers">The captured response headers, in order. Never <see langword="null"/>.</param>
    /// <param name="body">The response body bytes, or <see langword="null"/> for a vary marker.</param>
    /// <param name="createdAt">The instant the entry was produced, used to compute the served <c>Age</c>.</param>
    /// <param name="validFor">The time-to-live after which the store treats the entry as absent. Must be positive.</param>
    /// <param name="tags">The eviction tags this entry participates in. Never <see langword="null"/>.</param>
    /// <param name="varyBy">The response <c>Vary</c> field-names this representation depends on. Never <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/>, <paramref name="tags"/>, or <paramref name="varyBy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="validFor"/> is not positive.</exception>
    public OutputCacheEntry(
        HttpStatusCode statusCode,
        IReadOnlyList<OutputCacheHeader> headers,
        byte[]? body,
        DateTimeOffset createdAt,
        TimeSpan validFor,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> varyBy)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(varyBy);

        if (validFor <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(validFor), validFor, "The time-to-live must be positive.");
        }

        StatusCode = statusCode;
        Headers = headers;
        Body = body;
        CreatedAt = createdAt;
        ValidFor = validFor;
        Tags = tags;
        VaryBy = varyBy;
        Size = ComputeSize(headers, body, varyBy);
    }

    /// <summary>
    /// Gets the stored response status code. Meaningful only when the entry is a response
    /// (<see cref="IsVaryMarker"/> is <see langword="false"/>).
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the captured response headers, in order.
    /// </summary>
    public IReadOnlyList<OutputCacheHeader> Headers { get; }

    /// <summary>
    /// Gets the stored response body bytes, or <see langword="null"/> when the entry is a vary marker.
    /// </summary>
    public byte[]? Body { get; }

    /// <summary>
    /// Gets the instant the entry was produced. Used to compute the <c>Age</c> served on a hit.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets the time-to-live after which the store treats the entry as absent.
    /// </summary>
    public TimeSpan ValidFor { get; }

    /// <summary>
    /// Gets the eviction tags this entry participates in.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Gets the response <c>Vary</c> field-names this representation depends on. For a vary marker this
    /// is the set of request headers a lookup must fold into the secondary cache key.
    /// </summary>
    public IReadOnlyList<string> VaryBy { get; }

    /// <summary>
    /// Gets the logical size of the entry in bytes (body plus captured header text), used by a
    /// size-limited store for per-entry accounting.
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// Gets a value indicating whether the entry is a vary marker (no body) rather than a stored response.
    /// </summary>
    public bool IsVaryMarker => Body is null;

    private static long ComputeSize(IReadOnlyList<OutputCacheHeader> headers, byte[]? body, IReadOnlyList<string> varyBy)
    {
        // A generous byte estimate so a size-limited store accounts for the whole record, not just the
        // body: two bytes per UTF-16 char is an upper bound on the eventual wire/UTF-8 size, which is
        // the conservative direction for a capacity guard.
        long size = body?.LongLength ?? 0;

        for (int i = 0; i < headers.Count; i++)
        {
            OutputCacheHeader header = headers[i];
            size += header.Name.Length * 2L;

            IReadOnlyList<string?> values = header.Values;
            for (int v = 0; v < values.Count; v++)
            {
                size += (values[v]?.Length ?? 0) * 2L;
            }
        }

        for (int i = 0; i < varyBy.Count; i++)
        {
            size += varyBy[i].Length * 2L;
        }

        // A small fixed floor so a zero-length response still consumes a slot against the size limit.
        return size + 64;
    }
}
