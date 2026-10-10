using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Internal;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Pipeline branches that do not rejoin: path branches (<c>Map(path)</c>) and predicate branches
/// (<c>MapWhen</c>), and the path-base view a path branch publishes.
/// </summary>
/// <remarks>
/// <para>
/// A branch is a pipeline segment with its own middleware. <c>Map</c> and <c>MapWhen</c> branches do not
/// rejoin: they end in the standard terminal (<see cref="WebApplicationTerminal"/>), which runs an
/// endpoint selected before the branch (for example by <c>UseRouting</c> earlier in the pipeline) or ends
/// the request with a 404. A segment that rejoins is the Web root's <c>UseWhen</c>, and each branch here is
/// a <c>UseWhen</c> segment that ends in the terminal, so it composes against the root's seams alone.
/// </para>
/// <para>
/// Branches hold middleware, not routes: routes belong to the application's router
/// (<c>app.MapGet</c>, <c>app.MapGroup</c>), and per-endpoint policies are endpoint metadata. The
/// routing verbs therefore require the application builder and are not available on a branch.
/// </para>
/// </remarks>
public static class WebApplicationBranchingExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Branches requests whose path starts with <paramref name="path"/> (at a segment boundary,
        /// case-insensitively) into <paramref name="configure"/>'s pipeline. The branch does not rejoin.
        /// </summary>
        /// <remarks>
        /// Inside the branch, <see cref="IWebPathBaseFeature"/> records the matched prefix as the path base
        /// and the remaining path; <c>context.GetEffectivePath()</c> returns the remaining path. The request
        /// is not rewritten: <see cref="IHttpRequest.Path"/> keeps the full path.
        /// </remarks>
        /// <param name="path">The path prefix, for example <c>/static</c>. A trailing <c>/</c> is ignored.</param>
        /// <param name="configure">Configures the branch's middleware.</param>
        /// <returns>The containing pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is the root or not an origin-form path.</exception>
        public IWebApplicationPipelineBuilder Map(HttpPath path, Action<IWebApplicationPipelineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            string prefix = path.Value?.TrimEnd('/') ?? string.Empty;
            if (prefix.Length == 0 || prefix[0] != '/')
            {
                throw new ArgumentException(
                    $"A path branch needs an origin-form prefix below the root, such as '/static': '{path.Value}'.", nameof(path));
            }

            HttpPath prefixPath = new(prefix);

            // A nested branch matches against the path its enclosing branch sees, not the full path.
            return builder.UseWhen(
                context => MatchesPrefix(context.GetEffectivePath(), prefix),
                branch =>
                {
                    // First in the branch: install the path-base view for the rest of the branch, and
                    // restore the enclosing one when the branch returns.
                    branch.Use((WebApplicationMiddleware next) => context => InvokePathBranchAsync(context, prefix, prefixPath, next));
                    configure(branch);
                    branch.Run(WebApplicationTerminal.InvokeAsync);
                });
        }

        /// <summary>
        /// Branches requests that satisfy <paramref name="predicate"/> into
        /// <paramref name="configure"/>'s pipeline. The branch does not rejoin.
        /// </summary>
        /// <param name="predicate">Decides, per request, whether the branch runs.</param>
        /// <param name="configure">Configures the branch's middleware.</param>
        /// <returns>The containing pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder MapWhen(Func<IHttpContext, bool> predicate, Action<IWebApplicationPipelineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(configure);

            return builder.UseWhen(predicate, branch =>
            {
                configure(branch);
                branch.Run(WebApplicationTerminal.InvokeAsync);
            });
        }
    }

    extension(IHttpContext context)
    {
        /// <summary>
        /// Gets the prefixes the path branches this request entered matched
        /// (<see cref="IWebPathBaseFeature.PathBase"/>), or <see cref="HttpPath.Root"/> outside any path branch.
        /// </summary>
        /// <returns>The request's path base.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public HttpPath GetPathBase()
        {
            ArgumentNullException.ThrowIfNull(context);

            return context.Features.Get<IWebPathBaseFeature>()?.PathBase ?? HttpPath.Root;
        }

        /// <summary>
        /// Gets the request path as the current pipeline segment sees it: the path below the path base
        /// inside a path branch (<see cref="IWebPathBaseFeature.Path"/>), and <see cref="IHttpRequest.Path"/>
        /// outside one.
        /// </summary>
        /// <returns>The effective request path.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public HttpPath GetEffectivePath()
        {
            ArgumentNullException.ThrowIfNull(context);

            return context.Features.Get<IWebPathBaseFeature>()?.Path ?? context.Request.Path;
        }
    }

    // A segment-boundary, case-insensitive prefix match: '/static' matches '/static', '/static/' and
    // '/static/app.js', never '/staticx'. Allocation-free: it runs for every request that reaches the branch.
    private static bool MatchesPrefix(HttpPath path, string prefix)
    {
        string value = path.Value ?? "/";

        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (value.Length == prefix.Length || value[prefix.Length] == '/');
    }

    // The path below a matched prefix; it always starts with '/'.
    private static HttpPath RemovePrefix(HttpPath path, string prefix)
    {
        string value = path.Value ?? "/";

        return value.Length == prefix.Length ? HttpPath.Root : new HttpPath(value[prefix.Length..]);
    }

    private static async Task InvokePathBranchAsync(IHttpContext context, string prefix, HttpPath prefixPath, WebApplicationMiddleware branch)
    {
        IWebPathBaseFeature? outer = context.Features.Get<IWebPathBaseFeature>();
        HttpPath remainder = RemovePrefix(outer?.Path ?? context.Request.Path, prefix);
        HttpPath pathBase = (outer?.PathBase ?? HttpPath.Root).Concat(prefixPath);

        context.Features.Set<IWebPathBaseFeature>(new WebPathBaseFeature(pathBase, remainder));

        try
        {
            await branch.Invoke(context).ConfigureAwait(false);
        }
        finally
        {
            // Restore the enclosing view for the middleware outside the branch that observe the request
            // after it returns.
            context.Features.Set<IWebPathBaseFeature>(outer);
        }
    }
}
