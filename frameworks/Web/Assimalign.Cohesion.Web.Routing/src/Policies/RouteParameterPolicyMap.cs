using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

using Assimalign.Cohesion.Web.Routing.Patterns;

/// <summary>
/// Resolves inline parameter policy names into executable route parameter policies.
/// </summary>
public sealed class RouteParameterPolicyMap
{
    private readonly Dictionary<string, Func<string?, RouteParameterPolicy>> _factories;

    /// <summary>
    /// Creates an empty route parameter policy map.
    /// </summary>
    public RouteParameterPolicyMap()
    {
        _factories = new Dictionary<string, Func<string?, RouteParameterPolicy>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a copy of an existing route parameter policy map.
    /// </summary>
    /// <param name="other">The map to copy.</param>
    public RouteParameterPolicyMap(RouteParameterPolicyMap other)
    {
        ArgumentNullException.ThrowIfNull(other);

        _factories = new Dictionary<string, Func<string?, RouteParameterPolicy>>(other._factories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers a route parameter policy factory for an inline policy name.
    /// </summary>
    /// <param name="policyName">The inline policy name.</param>
    /// <param name="factory">The factory used to create the executable policy.</param>
    /// <returns>The current policy map.</returns>
    public RouteParameterPolicyMap Add(string policyName, Func<string?, RouteParameterPolicy> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        ArgumentNullException.ThrowIfNull(factory);

        _factories[policyName] = factory;
        return this;
    }

    /// <summary>
    /// Registers a fixed route parameter policy for an inline policy name.
    /// </summary>
    /// <param name="policyName">The inline policy name.</param>
    /// <param name="policy">The route parameter policy instance.</param>
    /// <returns>The current policy map.</returns>
    public RouteParameterPolicyMap Add(string policyName, RouteParameterPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);
        ArgumentNullException.ThrowIfNull(policy);

        return Add(policyName, _ => policy);
    }

    /// <summary>
    /// Attempts to resolve an executable policy from a route pattern policy reference.
    /// </summary>
    /// <param name="reference">The route pattern policy reference.</param>
    /// <param name="policy">The resolved executable policy.</param>
    /// <returns><see langword="true"/> when the policy was resolved; otherwise <see langword="false"/>.</returns>
    public bool TryResolve(RoutePatternParameterPolicyReference reference, out RouteParameterPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.ParameterPolicy is not null)
        {
            policy = reference.ParameterPolicy;
            return true;
        }

        return TryResolve(reference.Content, out policy);
    }

    /// <summary>
    /// Attempts to resolve an executable policy from inline policy text.
    /// </summary>
    /// <param name="policyText">The inline policy text.</param>
    /// <param name="policy">The resolved executable policy.</param>
    /// <returns><see langword="true"/> when the policy was resolved; otherwise <see langword="false"/>.</returns>
    public bool TryResolve(string? policyText, out RouteParameterPolicy? policy)
    {
        policy = null;

        if (!TryParsePolicy(policyText, out string policyName, out string? argument) ||
            !_factories.TryGetValue(policyName, out Func<string?, RouteParameterPolicy>? factory))
        {
            return false;
        }

        policy = factory(argument);
        return policy is not null;
    }

    /// <summary>
    /// Creates the default route parameter policy map used by router instances.
    /// </summary>
    /// <returns>A default route parameter policy map with the built-in policies registered.</returns>
    public static RouteParameterPolicyMap CreateDefault()
    {
        return new RouteParameterPolicyMap()
            .Add("int", static _ => new RegexRouteParameterPolicy(@"^-?\d+$"))
            .Add("long", static _ => new RegexRouteParameterPolicy(@"^-?\d+$"))
            .Add("alpha", static _ => new RegexRouteParameterPolicy(@"^[A-Za-z]+$"))
            .Add("bool", static _ => new RegexRouteParameterPolicy(@"^(true|false)$"))
            .Add("decimal", static _ => new RegexRouteParameterPolicy(@"^-?\d+(\.\d+)?$"))
            .Add("guid", static _ => new RegexRouteParameterPolicy(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[1-5][0-9A-Fa-f]{3}-[89ABab][0-9A-Fa-f]{3}-[0-9A-Fa-f]{12}$"))
            .Add("regex", static argument => CreateRegexPolicy(argument))
            .Add("range", static argument => CreateRangePolicy(argument))
            .Add("when", static argument => CreateRequiredRouteValuePolicy(argument));
    }

    private static RegexRouteParameterPolicy CreateRegexPolicy(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new InvalidOperationException("The 'regex' route parameter policy requires a regular expression pattern.");
        }

        return new RegexRouteParameterPolicy(argument);
    }

    private static RangeRouteParameterPolicy CreateRangePolicy(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new InvalidOperationException("The 'range' route parameter policy requires a 'min,max' argument.");
        }

        string[] boundaries = argument.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (boundaries.Length != 2 ||
            !long.TryParse(boundaries[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long minimum) ||
            !long.TryParse(boundaries[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long maximum))
        {
            throw new InvalidOperationException($"The 'range' route parameter policy argument '{argument}' is invalid.");
        }

        return new RangeRouteParameterPolicy(minimum, maximum);
    }

    private static RequiredValueRouteParameterPolicy CreateRequiredRouteValuePolicy(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new InvalidOperationException("The 'when' route parameter policy requires a 'key=value' argument.");
        }

        int separatorIndex = argument.IndexOf('=');
        if (separatorIndex <= 0 || separatorIndex == argument.Length - 1)
        {
            throw new InvalidOperationException($"The 'when' route parameter policy argument '{argument}' is invalid.");
        }

        string routeKey = argument[..separatorIndex].Trim();
        string expectedValue = argument[(separatorIndex + 1)..].Trim();

        return new RequiredValueRouteParameterPolicy(routeKey, expectedValue);
    }

    private static bool TryParsePolicy(string? policyText, out string policyName, out string? argument)
    {
        policyName = string.Empty;
        argument = null;

        if (string.IsNullOrWhiteSpace(policyText))
        {
            return false;
        }

        int openParenthesisIndex = policyText.IndexOf('(');
        if (openParenthesisIndex < 0)
        {
            policyName = policyText;
            return true;
        }

        if (!policyText.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        policyName = policyText[..openParenthesisIndex];
        argument = policyText[(openParenthesisIndex + 1)..^1];

        return !string.IsNullOrWhiteSpace(policyName);
    }
}
