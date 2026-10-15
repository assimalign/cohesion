using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A case-sensitive mount expression using parameter:&lt;name&gt;, literal:&lt;payload&gt;,
/// or &lt;source&gt;:&lt;key&gt; without normalizing its text.
/// </summary>
/// <remarks>
/// The resource-manifest contract compares source names and keys ordinally. A literal payload
/// may be empty or contain colons; a parameter name must be nonblank. Other sources have
/// exactly one colon separating nonblank source and key. Default is not a declared source.
/// </remarks>
public partial struct ResourceMountSource
{
    /// <summary>Determines whether a value follows the resource mount-source grammar.</summary>
    /// <param name="value">The candidate expression.</param>
    /// <param name="message">The refusal reason, or null when the expression is valid.</param>
    /// <returns>Whether the source expression is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is not null)
        {
            if (value.StartsWith("literal:", StringComparison.Ordinal))
            {
                message = null;
                return true;
            }

            int separator = value.IndexOf(':');
            if (separator > 0 && !string.IsNullOrWhiteSpace(value[(separator + 1)..]) &&
                (value.StartsWith("parameter:", StringComparison.Ordinal) ||
                 (!string.IsNullOrWhiteSpace(value[..separator]) &&
                  value.IndexOf(':', separator + 1) < 0)))
            {
                message = null;
                return true;
            }
        }

        message = ErrorMessages.ResourceMountSourceInvalid;
        return false;
    }

    /// <summary>Converts an optional expression, preserving null as an absent source.</summary>
    /// <param name="value">The expression, or null for omission.</param>
    /// <returns>The checked source expression, or null.</returns>
    /// <exception cref="ArgumentException">The expression is invalid.</exception>
    public static implicit operator ResourceMountSource?(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return new ResourceMountSource(value);
    }
}
