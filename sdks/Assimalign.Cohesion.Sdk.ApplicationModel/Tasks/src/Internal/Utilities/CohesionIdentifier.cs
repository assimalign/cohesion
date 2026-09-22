using System;
using System.Text;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks.Internal;

// Deviates from the repo namespace-matches-assembly rule per design decision: this
// ApplicationModel-owned source is linked into Gateway and retains its owning namespace.
internal static class CohesionIdentifier
{
    public static string ToPascalCase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var result = new StringBuilder(value.Length);
        int segmentStart = 0;
        for (int index = 0; index <= value.Length; index++)
        {
            bool separator = index == value.Length || !char.IsLetterOrDigit(value[index]);
            if (!separator)
            {
                continue;
            }

            if (index > segmentStart)
            {
                AppendSegment(result, value.AsSpan(segmentStart, index - segmentStart));
            }

            segmentStart = index + 1;
        }

        if (result.Length == 0)
        {
            return "Value";
        }

        if (char.IsDigit(result[0]))
        {
            result.Insert(0, '_');
        }

        return result.ToString();
    }

    private static void AppendSegment(StringBuilder result, ReadOnlySpan<char> segment)
    {
        if (segment.Length == 4 && segment.StartsWith("app", StringComparison.OrdinalIgnoreCase))
        {
            result.Append("App");
            result.Append(char.ToUpperInvariant(segment[3]));
            return;
        }

        result.Append(char.ToUpperInvariant(segment[0]));
        result.Append(segment[1..]);
    }
}
