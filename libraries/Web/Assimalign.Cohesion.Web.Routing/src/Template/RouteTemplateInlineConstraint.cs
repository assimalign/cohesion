
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Template;

using Assimalign.Cohesion.Web.Routing.Patterns;

/// <summary>
/// The parsed representation of an inline constraint in a route parameter.
/// </summary>
public class RouteTemplateInlineConstraint
{
    /// <summary>
    /// Creates a new instance of <see cref="RouteTemplateInlineConstraint"/>.
    /// </summary>
    /// <param name="constraint">The constraint text.</param>
    public RouteTemplateInlineConstraint(string constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        Constraint = constraint;
    }

    /// <summary>
    /// Creates a new <see cref="RouteTemplateInlineConstraint"/> instance given a <see cref="RoutePatternParameterPolicyReference"/>.
    /// </summary>
    /// <param name="other">A <see cref="RoutePatternParameterPolicyReference"/> instance.</param>
    public RouteTemplateInlineConstraint(RoutePatternParameterPolicyReference other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Constraint = other.Content!;
    }

    /// <summary>
    /// Gets the constraint text.
    /// </summary>
    public string Constraint { get; }
}
