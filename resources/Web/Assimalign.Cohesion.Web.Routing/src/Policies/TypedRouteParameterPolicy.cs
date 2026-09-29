using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Policies;

/// <summary>
/// Base class for a route parameter policy that both validates a route value and converts it to a
/// strongly-typed representation. The value is parsed <em>once</em>, using
/// <see cref="CultureInfo.InvariantCulture"/>; on success the typed value replaces the raw string in
/// the captured route values so binding, results, and other downstream consumers read the typed value
/// without re-parsing it.
/// </summary>
/// <remarks>
/// <para>
/// The built-in typed constraints (<c>int</c>, <c>long</c>, <c>decimal</c>, <c>double</c>,
/// <c>float</c>, <c>bool</c>, <c>guid</c>, <c>datetime</c>) all derive from this type. Custom
/// constraints contribute a typed conversion the same way: derive from
/// <see cref="TypedRouteParameterPolicy"/>, implement <see cref="TryConvert(string, out object?)"/>
/// and <see cref="ConversionType"/>, and register the policy through a
/// <see cref="RouteParameterPolicyMap"/>.
/// </para>
/// <para>
/// <see cref="Applies(RouteParameterPolicyContext)"/> is sealed: it owns the single-parse /
/// write-back protocol so the conversion happens exactly once per matched candidate. Derived types
/// supply only the parse.
/// </para>
/// </remarks>
public abstract class TypedRouteParameterPolicy : RouteParameterPolicy
{
    /// <summary>
    /// Gets the CLR type that a successful conversion produces (for example <see cref="int"/> or
    /// <see cref="Guid"/>). Used to recognize a value that has already been converted so it is not
    /// re-parsed.
    /// </summary>
    public abstract Type ConversionType { get; }

    /// <summary>
    /// Attempts to parse the raw route value text into this policy's typed representation, using the
    /// invariant culture.
    /// </summary>
    /// <param name="value">The raw route value text captured during matching.</param>
    /// <param name="converted">The typed value when parsing succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the value parses to the target type; otherwise <see langword="false"/>.</returns>
    public abstract bool TryConvert(string value, out object? converted);

    /// <inheritdoc />
    public sealed override bool Applies(RouteParameterPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.TryGetParameterValue(out object? raw) || raw is null)
        {
            return false;
        }

        // Already converted (for example by a typed default value or an earlier evaluation on this
        // candidate): accept without re-parsing so the conversion stays a single parse.
        if (ConversionType.IsInstanceOfType(raw))
        {
            return true;
        }

        if (raw is not string text)
        {
            // A non-string, non-target value was captured (an untyped default). Convert its invariant
            // string form once so the constraint still governs the value.
            text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (TryConvert(text, out object? converted))
        {
            // Parse once: write the typed value back into the route values in place of the string.
            context.SetParameterValue(converted);
            return true;
        }

        return false;
    }
}
