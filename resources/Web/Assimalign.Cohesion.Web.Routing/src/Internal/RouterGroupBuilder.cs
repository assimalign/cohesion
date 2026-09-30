using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// Default <see cref="IRouterGroupBuilder"/>. Composes the group prefix onto child templates as
/// raw text and re-parses the composed template, so every registered child is an ordinary
/// fully-composed <see cref="Route"/> in the underlying <see cref="IRouterBuilder"/> — the router
/// never sees the group.
/// </summary>
/// <remarks>
/// Two kinds of shared configuration compose differently. Parameter policies are resolved when a
/// child's template is parsed at registration, so they must be declared before the first child or
/// nested group (they freeze then). Metadata is composed when the route table is built
/// (<see cref="DeferredRouteMetadata"/>), so it applies to every child regardless of call order,
/// and nested groups read their ancestors' metadata through the parent chain rather than a
/// snapshot.
/// </remarks>
internal sealed class RouterGroupBuilder : IRouterGroupBuilder
{
    private readonly IRouterBuilder _routerBuilder;
    private readonly RouterGroupBuilder? _parent;
    private readonly RouteParameterPolicyMap _policyMap;
    private readonly List<object> _metadata = new();
    private readonly Lock _lock = new();
    private bool _policiesFrozen;

    // Set once a route of this group (or of a nested group) resolved its metadata, which happens when
    // the route table is built: group metadata attached afterwards could no longer apply.
    private bool _metadataSealed;

    internal RouterGroupBuilder(IRouterBuilder routerBuilder, RouterGroupBuilder? parent, string prefix)
    {
        _routerBuilder = routerBuilder;
        _parent = parent;

        Prefix = CombineTemplates(parent?.Prefix ?? string.Empty, NormalizeTemplate(prefix));

        if (Prefix.Length > 0)
        {
            // Fail fast at group creation: template syntax errors and parameter names duplicated
            // across nesting levels surface here rather than at the first child registration.
            RoutePatternParser.Parse(Prefix);
        }

        // Snapshot the parent's parameter policies. A copy (not a reference) keeps sibling groups and
        // the parent isolated from this group's own WithParameterPolicy calls.
        _policyMap = parent is null
            ? RouteParameterPolicyMap.CreateDefault()
            : new RouteParameterPolicyMap(parent._policyMap);
    }

    /// <inheritdoc />
    public string Prefix { get; }

    /// <inheritdoc />
    public IRouterGroupBuilder MapGroup(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        RouterGroupBuilder nested = new(_routerBuilder, this, prefix);

        // The nested group snapshotted this group's parameter policies; a later registration here
        // would silently not reach it, so freeze instead. Metadata needs no freeze: the nested group
        // reads it through the parent chain when the route table is built.
        _policiesFrozen = true;

        return nested;
    }

    /// <inheritdoc />
    public IRouterGroupBuilder WithMetadata(params object[] items)
    {
        DeferredRouteMetadata.Validate(items);

        lock (_lock)
        {
            if (_metadataSealed)
            {
                throw DeferredRouteMetadata.RouteTableBuilt();
            }

            _metadata.AddRange(items);
        }

        return this;
    }

    IRouterConventionBuilder IRouterConventionBuilder.WithMetadata(params object[] items) => WithMetadata(items);

    /// <inheritdoc />
    public IRouterGroupBuilder WithParameterPolicy(string policyName, RouteParameterPolicy policy)
    {
        ThrowIfPoliciesFrozen();

        _policyMap.Add(policyName, policy);
        return this;
    }

    /// <inheritdoc />
    public IRouterGroupBuilder WithParameterPolicy(string policyName, Func<string?, RouteParameterPolicy> factory)
    {
        ThrowIfPoliciesFrozen();

        _policyMap.Add(policyName, factory);
        return this;
    }

    /// <inheritdoc />
    public IRouterRouteBuilder Map(HttpMethod method, string template, IRouterRouteHandler handler)
    {
        return MapCore(new[] { method }, template, handler, metadata: null, policies: null);
    }

    /// <inheritdoc />
    public IRouterRouteBuilder Map(HttpMethod method, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return MapCore(new[] { method }, template, handler, metadata, policies: null);
    }

    /// <inheritdoc />
    public IRouterRouteBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler)
    {
        return MapCore(methods, template, handler, metadata: null, policies: null);
    }

    /// <inheritdoc />
    public IRouterRouteBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return MapCore(methods, template, handler, metadata, policies: null);
    }

    /// <inheritdoc />
    public IRouterRouteBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection? metadata, Action<RouteParameterPolicyMap> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        return MapCore(methods, template, handler, metadata, policies);
    }

    /// <summary>
    /// Appends the metadata of this group's ancestors (outermost first), then its own, and seals every
    /// group in the chain against further metadata. Called when a child route's metadata is resolved,
    /// as the route table is built.
    /// </summary>
    /// <param name="items">The list the metadata is appended to.</param>
    internal void CollectMetadata(List<object> items)
    {
        _parent?.CollectMetadata(items);

        lock (_lock)
        {
            _metadataSealed = true;
            items.AddRange(_metadata);
        }
    }

    private IRouterRouteBuilder MapCore(
        IEnumerable<HttpMethod> methods,
        string template,
        IRouterRouteHandler handler,
        IRouterRouteMetadataCollection? metadata,
        Action<RouteParameterPolicyMap>? policies)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(handler);

        // Registration-time composition: join the prefix and the child template as raw text and
        // re-parse, so the parser's own conflict rules govern the composed template and precedence
        // is computed over the full path.
        string composedTemplate = CombineTemplates(Prefix, NormalizeTemplate(template));
        RoutePattern pattern = RoutePatternParser.Parse(composedTemplate);

        RouteParameterPolicyMap policyMap = _policyMap;
        if (policies is not null)
        {
            // Route-level overrides act on a copy: re-registering a name the group registered
            // replaces it for this route only, leaving the group and its other children untouched.
            policyMap = new RouteParameterPolicyMap(_policyMap);
            policies(policyMap);
        }

        // Group items first, route-level items last, composed when the route table is built: the
        // collection's last-wins GetMetadata<T> makes the route-level (most specific) declaration
        // the override, whatever order the metadata calls were made in.
        DeferredRouteMetadata deferred = new(this, metadata);
        Route route = new(methods, pattern, policyMap, handler, deferred);

        _routerBuilder.Map(route);
        _policiesFrozen = true;

        return new RouterRouteBuilder(deferred);
    }

    private void ThrowIfPoliciesFrozen()
    {
        if (_policiesFrozen)
        {
            throw new InvalidOperationException(
                "The route group's parameter policies are frozen because a child route or nested group has " +
                "already been registered, and child templates resolve their inline policies when they are mapped. " +
                "Declare group-level parameter policies before mapping children so they apply to all child routes.");
        }
    }

    private static string NormalizeTemplate(string template)
    {
        ReadOnlySpan<char> span = template.AsSpan();

        if (span.StartsWith("~/", StringComparison.Ordinal))
        {
            span = span[2..];
        }

        span = span.Trim('/');

        return span.Length == template.Length ? template : span.ToString();
    }

    private static string CombineTemplates(string left, string right)
    {
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        return $"{left}/{right}";
    }
}
