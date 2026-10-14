namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// A rule target expanded for one request: URL text, with every capture percent-encoded for the part of
/// the URL it landed in.
/// </summary>
internal readonly struct RewriteTarget
{
    public RewriteTarget(string? scheme, string? authority, string path, string? query, string? fragment)
    {
        Scheme = scheme;
        Authority = authority;
        Path = path;
        Query = query;
        Fragment = fragment;
    }

    /// <summary>
    /// Gets the scheme of an absolute target (<c>http</c> or <c>https</c>), or <see langword="null"/> for a
    /// path target.
    /// </summary>
    public string? Scheme { get; }

    /// <summary>
    /// Gets the authority of an absolute target, or <see langword="null"/> for a path target.
    /// </summary>
    public string? Authority { get; }

    /// <summary>
    /// Gets the percent-encoded path: for a path target it starts with <c>/</c>; for an absolute target it is
    /// empty or starts with <c>/</c>.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the percent-encoded query without its <c>?</c>, or <see langword="null"/> when the target has no
    /// <c>?</c> and the request's query is kept. Empty when the target ends in <c>?</c>, which removes the query.
    /// </summary>
    public string? Query { get; }

    /// <summary>
    /// Gets the percent-encoded fragment without its <c>#</c>, or <see langword="null"/> when there is none.
    /// </summary>
    public string? Fragment { get; }
}
