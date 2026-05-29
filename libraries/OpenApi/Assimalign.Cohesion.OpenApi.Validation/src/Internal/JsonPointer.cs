using System.Text;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// Builds JSON Pointer strings (RFC 6901) used as diagnostic locations, escaping <c>~</c> and <c>/</c>
/// within each segment.
/// </summary>
internal static class JsonPointer
{
    internal static string Of(params string[] segments) => Append("#", segments);

    internal static string Append(string pointer, params string[] segments)
    {
        var builder = new StringBuilder(pointer);
        foreach (var segment in segments)
        {
            builder.Append('/');
            builder.Append(Escape(segment));
        }

        return builder.ToString();
    }

    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");
}
