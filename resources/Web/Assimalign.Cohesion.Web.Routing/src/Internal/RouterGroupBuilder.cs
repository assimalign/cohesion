using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Default <see cref="IRouterGroupBuilder"/>. Composes the group prefix onto child templates as
/// raw text and re-parses the composed template, so every registered child is an ordinary
/// fully-composed <see cref="Route"/> in the underlying <see cref="IRouterBuilder"/> — the router
/// never sees the group.
/// </summary>
internal sealed class RouterGroupBuilder : IRouterGroupBuilder
{
    private readonly IRouterBuilder _routerBuilder;
    private readonly RouteParameterPolicyMap _policyMap;
    private readonly List<object> _metadata;
    private bool _frozen;

    internal RouterGroupBuilder(IRouterBuilder routerBuilder, RouterGroupBuilder? parent, string prefix)
    {
        _routerBuilder = routerBuilder;

        Prefix = CombineTemplates(parent?.Prefix ?? string.Empty, NormalizeTemplate(prefix));

        if (Prefix.Length > 0)
        {
            // Fail fast at group creation: template syntax errors and parameter names duplicated
            // across nesting levels surface here rather than at the first child registration.
            RoutePatternParser.Parse(Prefix);
        }

        // Snapshot the parent's shared configuration. Copies (not references) keep sibling groups
        // and the parent isolated from this group's own WithMetadata/WithParameterPolicy calls.
        _policyMap = parent is null
            ? RouteParameterPolicyMap.CreateDefault()
            : new RouteParameterPolicyMap(parent._policyMap);
        _metadata = parent is null
            ? new List<object>()
            : new List<object>(parent._metadata);
    }

    /// <inheritdoc />
    public string Prefix { get; }

    /// <inheritdoc />
    public IRouterGroupBuilder MapGroup(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        RouterGroupBuilder nested = new(_routerBuilder, this, prefix);

        // The nested group snapshotted this group's shared configuration; later mutations here
        // would silently not reach it, so freeze instead.
        _frozen = true;

        return nested;
    }

    /// <inheritdoc />
    public IRouterGroupBuilder WithMetadata(params object[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ThrowIfFrozen();

        foreach (object item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("Endpoint metadata items must not be null.", nameof(items));
            }
        }

        _metadata.AddRange(items);
        return this;
    }

    /// <inheritdoc />
    public IRouterGroupBuilder WithParameterPolicy(string policyName, RouteParameterPolicy policy)
    {
        ThrowIfFrozen();

        _policyMap.Add(policyName, policy);
        return this;
    }

    /// <inheritdoc />
    public IRouterGroupBuilder WithParameterPolicy(string policyName, Func<string?, RouteParameterPolicy> factory)
    {
        ThrowIfFrozen();

        _policyMap.Add(policyName, factory);
        return this;
    }

    /// <inheritdoc />
    public IRouterGroupBuilder Map(HttpMethod method, string template, IRouterRouteHandler handler)
    {
        return MapCore(new[] { method }, template, handler, metadata: null, policies: null);
    }

    /// <inheritdoc />
    public IRouterGroupBuilder Map(HttpMethod method, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return MapCore(new[] { method }, template, handler, metadata, policies: null);
    }

    /// <inheritdoc />
    public IRouterGroupBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler)
    {
        return MapCore(methods, template, handler, metadata: null, policies: null);
    }

    /// <inheritdoc />
    public IRouterGroupBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return MapCore(methods, template, handler, metadata, policies: null);
    }

    /// <inheritdoc />
    public IRouterGroupBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler, IRouterRouteMetadataCollection? metadata, Action<RouteParameterPolicyMap> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        return MapCore(methods, template, handler, metadata, policies);
    }

    private IRouterGroupBuilder MapCore(
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

        Route route = new(methods, pattern, policyMap, handler, BuildMetadata(metadata));

        _routerBuilder.Map(route);
        _frozen = true;

        return this;
    }

    private IRouterRouteMetadataCollection BuildMetadata(IRouterRouteMetadataCollection? routeMetadata)
    {
        int routeCount = routeMetadata?.Count ?? 0;

        if (_metadata.Count == 0)
        {
            // Metadata collections are immutable by contract, so the route-level one is reusable as-is.
            return routeCount == 0
                ? RouterRouteMetadataCollection.Empty
                : routeMetadata!;
        }

        List<object> items = new(_metadata.Count + routeCount);
        items.AddRange(_metadata);

        if (routeMetadata is not null)
        {
            // Group items first, route-level items last: the collection's last-wins GetMetadata<T>
            // makes the route-level (most specific) declaration the override.
            items.AddRange(routeMetadata);
        }

        return new RouterRouteMetadataCollection(items);
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "The route group's shared configuration is frozen because a child route or nested group has " +
                "already been registered. Declare group-level metadata and parameter policies before mapping " +
                "children so they apply to all child routes.");
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
