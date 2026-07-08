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
    /// <remarks>
    /// The type policies (<c>int</c>, <c>long</c>, <c>decimal</c>, <c>double</c>, <c>float</c>,
    /// <c>bool</c>, <c>guid</c>, <c>datetime</c>) both validate the value and convert it to its typed
    /// representation, parsing once with the invariant culture. The remaining policies (<c>alpha</c>,
    /// <c>length</c>, <c>minlength</c>, <c>maxlength</c>, <c>min</c>, <c>max</c>, <c>range</c>,
    /// <c>regex</c>, <c>when</c>) validate the raw text and leave the value a string.
    /// </remarks>
    public static RouteParameterPolicyMap CreateDefault()
    {
        return new RouteParameterPolicyMap()
            // Typed conversions (validate + parse-once to the CLR type).
            .Add("int", static _ => new IntRouteParameterPolicy())
            .Add("long", static _ => new LongRouteParameterPolicy())
            .Add("decimal", static _ => new DecimalRouteParameterPolicy())
            .Add("double", static _ => new DoubleRouteParameterPolicy())
            .Add("float", static _ => new FloatRouteParameterPolicy())
            .Add("bool", static _ => new BoolRouteParameterPolicy())
            .Add("guid", static _ => new GuidRouteParameterPolicy())
            .Add("datetime", static _ => new DateTimeRouteParameterPolicy())
            // Text/value validators (leave the value a string).
            .Add("alpha", static _ => new RegexRouteParameterPolicy(@"^[A-Za-z]+$"))
            .Add("length", static argument => CreateLengthPolicy(argument))
            .Add("minlength", static argument => CreateMinLengthPolicy(argument))
            .Add("maxlength", static argument => CreateMaxLengthPolicy(argument))
            .Add("min", static argument => CreateMinPolicy(argument))
            .Add("max", static argument => CreateMaxPolicy(argument))
            .Add("range", static argument => CreateRangePolicy(argument))
            .Add("regex", static argument => CreateRegexPolicy(argument))
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

    private static LengthRouteParameterPolicy CreateLengthPolicy(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new InvalidOperationException("The 'length' route parameter policy requires an 'n' or 'min,max' argument.");
        }

        string[] parts = argument.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int exact) &&
            exact >= 0)
        {
            return new LengthRouteParameterPolicy(exact);
        }

        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minLength) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxLength) &&
            minLength >= 0 &&
            maxLength >= minLength)
        {
            return new LengthRouteParameterPolicy(minLength, maxLength);
        }

        throw new InvalidOperationException($"The 'length' route parameter policy argument '{argument}' is invalid.");
    }

    private static MinLengthRouteParameterPolicy CreateMinLengthPolicy(string? argument)
    {
        if (!TryParseNonNegativeInt(argument, out int minLength))
        {
            throw new InvalidOperationException($"The 'minlength' route parameter policy requires a non-negative integer argument, but was '{argument}'.");
        }

        return new MinLengthRouteParameterPolicy(minLength);
    }

    private static MaxLengthRouteParameterPolicy CreateMaxLengthPolicy(string? argument)
    {
        if (!TryParseNonNegativeInt(argument, out int maxLength))
        {
            throw new InvalidOperationException($"The 'maxlength' route parameter policy requires a non-negative integer argument, but was '{argument}'.");
        }

        return new MaxLengthRouteParameterPolicy(maxLength);
    }

    private static MinRouteParameterPolicy CreateMinPolicy(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument) ||
            !long.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long min))
        {
            throw new InvalidOperationException($"The 'min' route parameter policy requires an integer argument, but was '{argument}'.");
        }

        return new MinRouteParameterPolicy(min);
    }

    private static MaxRouteParameterPolicy CreateMaxPolicy(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument) ||
            !long.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long max))
        {
            throw new InvalidOperationException($"The 'max' route parameter policy requires an integer argument, but was '{argument}'.");
        }

        return new MaxRouteParameterPolicy(max);
    }

    private static bool TryParseNonNegativeInt(string? argument, out int value)
    {
        if (!string.IsNullOrWhiteSpace(argument) &&
            int.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= 0)
        {
            return true;
        }

        value = 0;
        return false;
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
