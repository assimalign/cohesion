using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Validates a route parameter using a regular expression.
/// </summary>
internal sealed class RegexRouteParameterPolicy : RouteParameterPolicy
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<Regex>? _regexFactory;
    private Regex? _constraint;

    /// <summary>
    /// Constructor for a <see cref="RegexRouteParameterPolicy"/> given a <paramref name="regex"/>.
    /// </summary>
    /// <param name="regex">A <see cref="Regex"/> instance to use as a constraint.</param>
    internal RegexRouteParameterPolicy(Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);

        _constraint = regex;
    }

    /// <summary>
    /// Constructor for a <see cref="RegexRouteParameterPolicy"/> given a <paramref name="regexPattern"/>.
    /// </summary>
    /// <param name="regexPattern">A string containing the regex pattern.</param>
    internal RegexRouteParameterPolicy(
        [StringSyntax(StringSyntaxAttribute.Regex, RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase)]
        string regexPattern)
    {
        ArgumentNullException.ThrowIfNull(regexPattern);

        // Create regex instance lazily to avoid compiling regexes at app startup. Delay creation until Constraint is first evaluated.
        // The regex instance is created by a delegate here to allow the regex engine to be trimmed when this constructor is trimmed.
        _regexFactory = () => new Regex(
            regexPattern,
            RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase,
            RegexMatchTimeout);
    }

    /// <summary>
    /// Gets the regular expression used in the route constraint.
    /// </summary>
    public Regex Constraint
    {
        get
        {
            if (_constraint is null)
            {
                Debug.Assert(_regexFactory is not null);

                // This is not thread-safe. No side effect, but multiple instances of a regex instance could be created from a burst of requests.
                _constraint = _regexFactory();
            }

            return _constraint;
        }
    }

    /// <inheritdoc />
    public override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetParameterValue(out object? routeValue) && routeValue is not null)
        {
            string parameterValueString = Convert.ToString(routeValue, CultureInfo.InvariantCulture)!;
            return Constraint.IsMatch(parameterValueString);
        }

        return false;
    }
}
