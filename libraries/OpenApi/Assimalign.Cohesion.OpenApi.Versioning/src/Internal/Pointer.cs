using System.Text;

namespace Assimalign.Cohesion.OpenApi.Versioning;

/// <summary>
/// Builds JSON Pointers (RFC 6901) for diagnostic locations, escaping <c>~</c> and <c>/</c> in tokens.
/// </summary>
internal static class Pointer
{
    internal static string Of(params string[] tokens) => Append("#", tokens);

    internal static string Append(string pointer, params string[] tokens)
    {
        var builder = new StringBuilder(pointer);
        foreach (var token in tokens)
        {
            builder.Append('/').Append(Escape(token));
        }

        return builder.ToString();
    }

    private static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");
}
