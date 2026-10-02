using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Pipeline branching: path and predicate branches, conditional segments that rejoin the pipeline, and
/// terminal middleware.
/// </summary>
/// <remarks>
/// <para>
/// A branch is a pipeline segment with its own middleware. <c>Map</c> and <c>MapWhen</c> branches do not
/// rejoin: they end in the standard terminal (<see cref="WebApplicationTerminal"/>), which runs an
/// endpoint selected before the branch (for example by <c>UseRouting</c> earlier in the pipeline) or ends
/// the request with a 404. A <c>UseWhen</c> segment rejoins: its last middleware's <c>next</c> is the
/// rest of the containing pipeline.
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

            WebApplicationBranchBuilder branch = new();
            configure(branch);

            HttpPath prefixPath = new(prefix);

            return builder.Use((IWebApplicationContext application, WebApplicationMiddleware next) =>
            {
                WebApplicationMiddleware branchPipeline = branch.Compose(application, WebApplicationTerminal.InvokeAsync);

                // A nested branch matches against the path its enclosing branch sees, not the full path.
                return context => TryRemovePrefix(context.GetEffectivePath(), prefix, out HttpPath remainder)
                    ? InvokePathBranchAsync(context, prefixPath, remainder, branchPipeline)
                    : next.Invoke(context);
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

            WebApplicationBranchBuilder branch = new();
            configure(branch);

            return builder.Use((IWebApplicationContext application, WebApplicationMiddleware next) =>
            {
                WebApplicationMiddleware branchPipeline = branch.Compose(application, WebApplicationTerminal.InvokeAsync);

                return context => predicate(context) ? branchPipeline.Invoke(context) : next.Invoke(context);
            });
        }

        /// <summary>
        /// Runs <paramref name="configure"/>'s middleware for requests that satisfy
        /// <paramref name="predicate"/>, then rejoins the containing pipeline: the segment's last
        /// middleware continues into the middleware registered after this call.
        /// </summary>
        /// <param name="predicate">Decides, per request, whether the segment runs.</param>
        /// <param name="configure">Configures the segment's middleware.</param>
        /// <returns>The containing pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseWhen(Func<IHttpContext, bool> predicate, Action<IWebApplicationPipelineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(configure);

            WebApplicationBranchBuilder branch = new();
            configure(branch);

            return builder.Use((IWebApplicationContext application, WebApplicationMiddleware next) =>
            {
                WebApplicationMiddleware segment = branch.Compose(application, next);

                return context => predicate(context) ? segment.Invoke(context) : next.Invoke(context);
            });
        }

        /// <summary>
        /// Adds terminal middleware: <paramref name="terminal"/> handles every request that reaches it, and
        /// nothing registered after it runs.
        /// </summary>
        /// <param name="terminal">The terminal handler.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="terminal"/> is <see langword="null"/>.</exception>
        public void Run(WebApplicationMiddleware terminal)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(terminal);

            builder.Use((WebApplicationMiddleware _) => terminal);
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
    // '/static/app.js', never '/staticx'. The remainder always starts with '/'.
    private static bool TryRemovePrefix(HttpPath path, string prefix, out HttpPath remainder)
    {
        string value = path.Value ?? "/";

        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            remainder = default;
            return false;
        }

        if (value.Length == prefix.Length)
        {
            remainder = HttpPath.Root;
            return true;
        }

        if (value[prefix.Length] != '/')
        {
            remainder = default;
            return false;
        }

        remainder = new HttpPath(value[prefix.Length..]);
        return true;
    }

    private static async Task InvokePathBranchAsync(IHttpContext context, HttpPath prefix, HttpPath remainder, WebApplicationMiddleware branch)
    {
        IWebPathBaseFeature? outer = context.Features.Get<IWebPathBaseFeature>();
        HttpPath pathBase = (outer?.PathBase ?? HttpPath.Root).Concat(prefix);

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
