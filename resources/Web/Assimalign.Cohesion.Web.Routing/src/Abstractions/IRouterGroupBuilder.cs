using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Composes a path prefix, shared parameter policies, and shared endpoint metadata onto child
/// routes at registration time (the <c>MapGroup</c> model).
/// </summary>
/// <remarks>
/// <para>
/// A route group is a <em>builder-time</em> composition tool: every child route registered through
/// the group is stored as a single fully-composed route whose template is the group prefix joined
/// with the child template. The composed template is parsed once, at registration, by the ordinary
/// route-template parser — so the router evaluates grouped routes exactly like directly-mapped
/// ones, with no per-request prefix matching, and all template conflict rules (duplicate parameter
/// names across the prefix and the child, catch-all placement, separator rules) are enforced by
/// the parser's existing semantics. Because the prefix segments are part of the composed pattern,
/// precedence is computed over the full path: a literal contributed by a group still outranks a
/// parameter at the same depth.
/// </para>
/// <para>
/// The two kinds of shared configuration follow different ordering rules (#1055):
/// </para>
/// <list type="bullet">
/// <item><b>Metadata is order-independent.</b> <see cref="WithMetadata"/> and every feature verb
/// built on <see cref="IRouterConventionBuilder"/> (for example <c>RequireRateLimiting</c>) apply to
/// every child route of the group and of its nested groups, whether the child was mapped before or
/// after the call, because metadata is composed when the route table is built. Attaching metadata
/// after the route table was built throws.</item>
/// <item><b>Parameter policies are declared first.</b> A child resolves its inline policies when
/// its template is parsed at registration, so the <see cref="WithParameterPolicy(string, RouteParameterPolicy)"/>
/// overloads may only be called before the group maps its first child route or nested group.
/// Afterwards the group's policies are frozen and further calls throw, which guarantees every child
/// resolves against the same policy set.</item>
/// </list>
/// <para>
/// Overrides are child-over-group, deterministically: group metadata items are placed
/// <em>before</em> route-level items in each child's <see cref="IRouterRouteMetadataCollection"/>,
/// so the collection's last-wins <c>GetMetadata&lt;T&gt;</c> rule resolves the route-level
/// (most specific) declaration; nested groups layer outer items before inner items. A route-level
/// parameter-policy registration for a name the group also registered replaces the group's
/// registration for that route only.
/// </para>
/// </remarks>
public interface IRouterGroupBuilder : IRouterConventionBuilder
{
    /// <summary>
    /// Gets the full composed route-template prefix applied to child routes, including every
    /// ancestor group's prefix.
    /// </summary>
    /// <remarks>
    /// The prefix is normalized: leading <c>~/</c> or <c>/</c> and trailing <c>/</c> are removed
    /// (for example <c>"api/v1"</c>). An empty string identifies a prefix-less group used purely
    /// to share parameter policies or endpoint metadata.
    /// </remarks>
    string Prefix { get; }

    /// <summary>
    /// Creates a nested route group whose prefix is this group's prefix joined with
    /// <paramref name="prefix"/>, which inherits a snapshot of this group's shared parameter
    /// policies and, when the route table is built, all of this group's endpoint metadata.
    /// </summary>
    /// <param name="prefix">
    /// The route-template prefix of the nested group, relative to this group. May contain
    /// parameters (for example <c>{tenant}/api</c>) and may be empty to share only policies and
    /// metadata.
    /// </param>
    /// <returns>The nested route group builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="prefix"/> is <see langword="null"/>.</exception>
    /// <exception cref="Exceptions.RoutePatternException">
    /// The composed prefix is not a valid route template (for example a syntax error, or a
    /// parameter name duplicated across the nesting levels).
    /// </exception>
    /// <remarks>
    /// Creating a nested group freezes this group's parameter policies (the nested group snapshots
    /// them), so declare shared policies before nesting. Metadata stays open: metadata attached to
    /// this group later still reaches the nested group's routes.
    /// </remarks>
    IRouterGroupBuilder MapGroup(string prefix);

    /// <summary>
    /// Adds shared endpoint metadata applied to every child route of this group and of its nested
    /// groups, including children mapped before this call.
    /// </summary>
    /// <param name="items">The metadata items to share. Must not contain <see langword="null"/> entries.</param>
    /// <returns>The current route group builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> contains a <see langword="null"/> entry.</exception>
    /// <exception cref="InvalidOperationException">
    /// The route table has already been built, so the metadata could no longer apply.
    /// </exception>
    /// <remarks>
    /// Group items are placed before route-level items in each child's metadata collection, so the
    /// last-wins <c>GetMetadata&lt;T&gt;</c> rule lets a route-level item of the same type override
    /// the group's.
    /// </remarks>
    new IRouterGroupBuilder WithMetadata(params object[] items);

    /// <summary>
    /// Registers a shared route parameter policy for an inline policy name, applied when resolving
    /// the inline policies of every child route registered through this group (and inherited by
    /// nested groups).
    /// </summary>
    /// <param name="policyName">The inline policy name referenced from child templates (for example <c>version</c> in <c>{v:version}</c>).</param>
    /// <param name="policy">The route parameter policy instance.</param>
    /// <returns>The current route group builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policyName"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// A child route or nested group has already been registered — the group's parameter
    /// policies are frozen.
    /// </exception>
    /// <remarks>
    /// Registering a name that is already registered (a built-in, or an outer group's registration)
    /// replaces it for this group's children — nested/child registrations deterministically
    /// override group-level ones.
    /// </remarks>
    IRouterGroupBuilder WithParameterPolicy(string policyName, RouteParameterPolicy policy);

    /// <summary>
    /// Registers a shared route parameter policy factory for an inline policy name, applied when
    /// resolving the inline policies of every child route registered through this group (and
    /// inherited by nested groups).
    /// </summary>
    /// <param name="policyName">The inline policy name referenced from child templates.</param>
    /// <param name="factory">The factory used to create the executable policy from the inline argument text.</param>
    /// <returns>The current route group builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policyName"/> or <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// A child route or nested group has already been registered — the group's parameter
    /// policies are frozen.
    /// </exception>
    IRouterGroupBuilder WithParameterPolicy(string policyName, Func<string?, RouteParameterPolicy> factory);

    /// <summary>
    /// Registers a child route whose template is this group's prefix joined with
    /// <paramref name="template"/>.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="template">
    /// The child route template, relative to the group prefix. An empty string maps the group
    /// prefix itself.
    /// </param>
    /// <param name="handler">The handler invoked when the composed route matches.</param>
    /// <returns>The mapped route's builder, for attaching route-level metadata.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/> or <paramref name="handler"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exceptions.RoutePatternException">
    /// The composed template is not a valid route template (for example a parameter name duplicated
    /// between the prefix and the child, or a prefix catch-all followed by child segments).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The composed template references an inline policy name unknown to the group's policy map.
    /// </exception>
    IRouterRouteBuilder Map(HttpMethod method, string template, IRouterRouteHandler handler);

    /// <summary>
    /// Registers a child route with route-level endpoint metadata whose template is this group's
    /// prefix joined with <paramref name="template"/>.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="template">
    /// The child route template, relative to the group prefix. An empty string maps the group
    /// prefix itself.
    /// </param>
    /// <param name="handler">The handler invoked when the composed route matches.</param>
    /// <param name="metadata">
    /// Route-level endpoint metadata, appended after the group's shared metadata so it overrides
    /// group items under the last-wins rule.
    /// </param>
    /// <returns>The mapped route's builder, for attaching route-level metadata.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/>, <paramref name="handler"/>, or <paramref name="metadata"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
    /// <exception cref="InvalidOperationException">
    /// The composed template references an inline policy name unknown to the group's policy map.
    /// </exception>
    IRouterRouteBuilder Map(HttpMethod method, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata);

    /// <summary>
    /// Registers a child route accepting multiple HTTP methods whose template is this group's
    /// prefix joined with <paramref name="template"/>.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="template">
    /// The child route template, relative to the group prefix. An empty string maps the group
    /// prefix itself.
    /// </param>
    /// <param name="handler">The handler invoked when the composed route matches.</param>
    /// <returns>The mapped route's builder, for attaching route-level metadata.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="methods"/>, <paramref name="template"/>, or <paramref name="handler"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
    /// <exception cref="InvalidOperationException">
    /// The composed template references an inline policy name unknown to the group's policy map.
    /// </exception>
    IRouterRouteBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler);

    /// <summary>
    /// Registers a child route accepting multiple HTTP methods, with route-level endpoint metadata,
    /// whose template is this group's prefix joined with <paramref name="template"/>.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="template">
    /// The child route template, relative to the group prefix. An empty string maps the group
    /// prefix itself.
    /// </param>
    /// <param name="handler">The handler invoked when the composed route matches.</param>
    /// <param name="metadata">
    /// Route-level endpoint metadata, appended after the group's shared metadata so it overrides
    /// group items under the last-wins rule.
    /// </param>
    /// <returns>The mapped route's builder, for attaching route-level metadata.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="methods"/>, <paramref name="template"/>, <paramref name="handler"/>, or
    /// <paramref name="metadata"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
    /// <exception cref="InvalidOperationException">
    /// The composed template references an inline policy name unknown to the group's policy map.
    /// </exception>
    IRouterRouteBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata);

    /// <summary>
    /// Registers a child route accepting multiple HTTP methods, with optional route-level endpoint
    /// metadata and route-level parameter-policy overrides, whose template is this group's prefix
    /// joined with <paramref name="template"/>.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="template">
    /// The child route template, relative to the group prefix. An empty string maps the group
    /// prefix itself.
    /// </param>
    /// <param name="handler">The handler invoked when the composed route matches.</param>
    /// <param name="metadata">
    /// Optional route-level endpoint metadata, appended after the group's shared metadata so it
    /// overrides group items under the last-wins rule.
    /// </param>
    /// <param name="policies">
    /// Configures a copy of the group's parameter policy map for this route only. Registering a
    /// policy name the group also registered replaces the group's registration for this route —
    /// the deterministic child-over-group override for parameter policies.
    /// </param>
    /// <returns>The mapped route's builder, for attaching route-level metadata.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="methods"/>, <paramref name="template"/>, <paramref name="handler"/>, or
    /// <paramref name="policies"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
    /// <exception cref="InvalidOperationException">
    /// The composed template references an inline policy name unknown to the configured policy map.
    /// </exception>
    IRouterRouteBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection? metadata, Action<RouteParameterPolicyMap> policies);
}
