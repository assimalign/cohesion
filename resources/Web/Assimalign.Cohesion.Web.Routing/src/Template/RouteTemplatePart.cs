
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Assimalign.Cohesion.Web.Routing.Template;

using Assimalign.Cohesion.Web.Routing.Patterns;

/// <summary>
/// Represents a part of a route template segment.
/// </summary>
[DebuggerDisplay("{DebuggerToString()}")]
public class RouteTemplatePart
{
    /// <summary>
    /// Constructs a new <see cref="RouteTemplatePart"/> instance.
    /// </summary>
    public RouteTemplatePart()
    {
    }

    /// <summary>
    /// Constructs a new <see cref="RouteTemplatePart"/> instance given a <paramref name="other"/>.
    /// </summary>
    /// <param name="other">A <see cref="RoutePatternSegment"/> instance representing the route part.</param>
    public RouteTemplatePart(RoutePatternSegment other)
    {
        IsLiteral = other.IsLiteral || other.IsSeparator;
        IsParameter = other.IsParameter;

        if (other.IsLiteral && other is RoutePatternLiteralSegment literal)
        {
            Text = literal.Content;
        }
        else if (other.IsParameter && other is RoutePatternParameterSegment parameter)
        {
            Name = parameter.Name;
            IsCatchAll = parameter.IsCatchAll;
            IsOptional = parameter.IsOptional;
            DefaultValue = parameter.Default;
            InlineConstraints = parameter.ParameterPolicies.Select(policy => new RouteTemplateInlineConstraint(policy));
        }
        else if (other.IsSeparator && other is RoutePatternSeparatorSegment separator)
        {
            Text = separator.Content;
            IsOptionalSeperator = true;
        }
        else
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Create a <see cref="RouteTemplatePart"/> representing a literal route part.
    /// </summary>
    /// <param name="text">The text of the literal route part.</param>
    /// <returns>A <see cref="RouteTemplatePart"/> instance.</returns>
    public static RouteTemplatePart CreateLiteral(string text)
    {
        return new RouteTemplatePart
        {
            IsLiteral = true,
            Text = text,
        };
    }

    /// <summary>
    /// Creates a <see cref="RouteTemplatePart"/> representing a parameter part.
    /// </summary>
    /// <param name="name">The name of the parameter.</param>
    /// <param name="isCatchAll"><see langword="true"/> if the parameter is a catch-all parameter.</param>
    /// <param name="isOptional"><see langword="true"/> if the parameter is an optional parameter.</param>
    /// <param name="defaultValue">The default value of the parameter.</param>
    /// <param name="inlineConstraints">A collection of constraints associated with the parameter.</param>
    /// <returns>A <see cref="RouteTemplatePart"/> instance.</returns>
    public static RouteTemplatePart CreateParameter(
        string name,
        bool isCatchAll,
        bool isOptional,
        object? defaultValue,
        IEnumerable<RouteTemplateInlineConstraint>? inlineConstraints)
    {
        ArgumentNullException.ThrowIfNull(name);

        return new RouteTemplatePart
        {
            IsParameter = true,
            Name = name,
            IsCatchAll = isCatchAll,
            IsOptional = isOptional,
            DefaultValue = defaultValue,
            InlineConstraints = inlineConstraints ?? Enumerable.Empty<RouteTemplateInlineConstraint>(),
        };
    }

    /// <summary>
    /// <see langword="true"/> if the route part is a catch-all part.
    /// </summary>
    public bool IsCatchAll { get; private set; }

    /// <summary>
    /// <see langword="true"/> if the route part represents a literal value.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Text))]
    public bool IsLiteral { get; private set; }

    /// <summary>
    /// <see langword="true"/> if the route part represents a parameterized value.
    /// </summary>
    public bool IsParameter { get; private set; }

    /// <summary>
    /// <see langword="true"/> if the route part represents an optional part.
    /// </summary>
    public bool IsOptional { get; private set; }

    /// <summary>
    /// <see langword="true"/> if the route part represents an optional separator.
    /// </summary>
    public bool IsOptionalSeperator { get; set; }

    /// <summary>
    /// The name of the route parameter, when the part is parameterized.
    /// </summary>
    public string? Name { get; private set; }

    /// <summary>
    /// The literal or separator text for the route part.
    /// </summary>
    public string? Text { get; private set; }

    /// <summary>
    /// The default value for route parameters.
    /// </summary>
    public object? DefaultValue { get; private set; }

    /// <summary>
    /// The constraints associated with a route parameter.
    /// </summary>
    public IEnumerable<RouteTemplateInlineConstraint> InlineConstraints { get; private set; } = Enumerable.Empty<RouteTemplateInlineConstraint>();

    internal string? DebuggerToString()
    {
        if (IsParameter)
        {
            return "{" + (IsCatchAll ? "*" : string.Empty) + Name + (IsOptional ? "?" : string.Empty) + "}";
        }

        return Text;
    }

    /// <summary>
    /// Creates a <see cref="RoutePatternSegment"/> for the route part designated by this template part.
    /// </summary>
    /// <returns>A <see cref="RoutePatternSegment"/> instance.</returns>
    public RoutePatternSegment ToRoutePatternPart()
    {
        if (IsLiteral && IsOptionalSeperator)
        {
            return RoutePatternFactory.SeparatorPart(Text!);
        }

        if (IsLiteral)
        {
            return RoutePatternFactory.LiteralPart(Text!);
        }

        RoutePatternParameterKind kind = IsCatchAll
            ? RoutePatternParameterKind.CatchAll
            : IsOptional
                ? RoutePatternParameterKind.Optional
                : RoutePatternParameterKind.Standard;

        IEnumerable<RoutePatternParameterPolicyReference> constraints =
            InlineConstraints.Select(constraint => new RoutePatternParameterPolicyReference(constraint.Constraint));

        return RoutePatternFactory.ParameterPart(Name!, DefaultValue, kind, constraints);
    }
}
