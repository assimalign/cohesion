using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Web.Routing.Constraints;

using Assimalign.Cohesion.Http;

public class RegexRouteConstraint : IRouteParameterConstraintPolicy
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<Regex>? _regexFactory;
    private Regex? _constraint;

    /// <summary>
    /// Constructor for a <see cref="RegexRouteConstraint"/> given a <paramref name="regex"/>.
    /// </summary>
    /// <param name="regex">A <see cref="Regex"/> instance to use as a constraint.</param>
    public RegexRouteConstraint(Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);

        _constraint = regex;
    }

    /// <summary>
    /// Constructor for a <see cref="RegexRouteConstraint"/> given a <paramref name="regexPattern"/>.
    /// </summary>
    /// <param name="regexPattern">A string containing the regex pattern.</param>
    public RegexRouteConstraint(
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
    public bool Match(
        IHttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        ArgumentNullException.ThrowIfNull(routeKey);
        ArgumentNullException.ThrowIfNull(values);

        if (values.TryGetValue(routeKey, out var routeValue)
            && routeValue != null)
        {
            var parameterValueString = Convert.ToString(routeValue, CultureInfo.InvariantCulture)!;

            return Constraint.IsMatch(parameterValueString);
        }

        return false;
    }


    //bool IParameterLiteralNodeMatchingPolicy.MatchesLiteral(string parameterName, string literal)
    //{
    //    return Constraint.IsMatch(literal);
    //}

}
