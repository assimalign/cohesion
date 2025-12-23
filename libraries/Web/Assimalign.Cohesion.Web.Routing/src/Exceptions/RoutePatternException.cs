using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing;

public sealed class RoutePatternException : Exception
{
    /// <summary>
    /// Creates a new instance of <see cref="RoutePatternException"/>.
    /// </summary>
    /// <param name="pattern">The route pattern as raw text.</param>
    /// <param name="message">The exception message.</param>
    public RoutePatternException([StringSyntax("Route")] string pattern, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(message);

        Pattern = pattern;
    }

    /// <summary>
    /// Gets the route pattern associated with this exception.
    /// </summary>
    public string Pattern { get; }
}
