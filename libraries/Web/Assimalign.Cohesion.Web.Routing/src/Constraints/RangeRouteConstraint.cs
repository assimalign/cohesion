using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Constraints;

using Assimalign.Cohesion.Http;

public class RangeRouteConstraint : IRouteParameterConstraintPolicy
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RangeRouteConstraint" /> class.
    /// </summary>
    /// <param name="min">The minimum value.</param>
    /// <param name="max">The maximum value.</param>
    /// <remarks>The minimum value should be less than or equal to the maximum value.</remarks>
    public RangeRouteConstraint(long min, long max)
    {
        if (min > max)
        {
            var errorMessage = "";// Resources.FormatRangeConstraint_MinShouldBeLessThanOrEqualToMax("min", "max");
            throw new ArgumentOutOfRangeException(nameof(min), min, errorMessage);
        }

        Min = min;
        Max = max;
    }

    /// <summary>
    /// Gets the minimum allowed value of the route parameter.
    /// </summary>
    public long Min { get; private set; }

    /// <summary>
    /// Gets the maximum allowed value of the route parameter.
    /// </summary>
    public long Max { get; private set; }

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

        if (values.TryGetValue(routeKey, out var value) && value != null)
        {
            var valueString = Convert.ToString(value, CultureInfo.InvariantCulture);
            return CheckConstraintCore(valueString);
        }

        return false;
    }

    private bool CheckConstraintCore(string? valueString)
    {
        if (long.TryParse(valueString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return longValue >= Min && longValue <= Max;
        }
        return false;
    }


    //bool IParameterLiteralNodeMatchingPolicy.MatchesLiteral(string parameterName, string literal)
    //{
    //    return CheckConstraintCore(literal);
    //}

}
