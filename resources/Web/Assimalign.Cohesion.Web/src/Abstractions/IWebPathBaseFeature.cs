using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The view of the request path inside the <c>Map(path, branch)</c> branches the request entered: the
/// prefixes the branches matched (<see cref="PathBase"/>) and the path below them (<see cref="Path"/>).
/// </summary>
/// <remarks>
/// <para>
/// A path branch mounts middleware at a sub-path. Inside <c>app.Map("/static", branch)</c>, a request to
/// <c>/static/app.js</c> has a <see cref="PathBase"/> of <c>/static</c> and a <see cref="Path"/> of
/// <c>/app.js</c>. Nested branches append their prefixes, outermost first. When a branch returns, the
/// view it installed is removed again.
/// </para>
/// <para>
/// The request itself is never rewritten: <see cref="IHttpRequest.Path"/> keeps the full path, as the
/// Web area's effective-value model requires (the same model forwarded headers follow). Middleware that
/// can be mounted in a branch reads the path through <c>context.GetEffectivePath()</c>, which returns
/// <see cref="Path"/> inside a branch and the request path outside one, and builds absolute URLs from the
/// full <see cref="IHttpRequest.Path"/>. The feature is absent outside any path branch.
/// </para>
/// </remarks>
public interface IWebPathBaseFeature : IHttpFeature
{
    /// <summary>
    /// Gets the prefixes the entered branches matched, outermost first (for example <c>/api/v1</c>).
    /// </summary>
    HttpPath PathBase { get; }

    /// <summary>
    /// Gets the request path below <see cref="PathBase"/>: <see cref="HttpPath.Root"/> when the request
    /// path is exactly the base, and otherwise a path that starts with <c>/</c>.
    /// </summary>
    HttpPath Path { get; }
}
