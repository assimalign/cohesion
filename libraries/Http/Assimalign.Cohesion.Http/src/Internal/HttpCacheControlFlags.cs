using System;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The valueless (boolean) <c>Cache-Control</c> directives, packed into a single field so a
/// parsed <see cref="HttpCacheControl"/> stays compact. Directives that carry a delta-seconds or
/// field-list argument are stored separately on the value object.
/// </summary>
[Flags]
internal enum HttpCacheControlFlags
{
    None = 0,
    NoStore = 1 << 0,
    NoCache = 1 << 1,
    NoTransform = 1 << 2,
    Public = 1 << 3,
    Private = 1 << 4,
    MustRevalidate = 1 << 5,
    ProxyRevalidate = 1 << 6,
    MustUnderstand = 1 << 7,
    Immutable = 1 << 8,
    OnlyIfCached = 1 << 9,

    /// <summary>The <c>max-stale</c> directive was present (its value may be absent, meaning any staleness is accepted).</summary>
    MaxStale = 1 << 10,
}
