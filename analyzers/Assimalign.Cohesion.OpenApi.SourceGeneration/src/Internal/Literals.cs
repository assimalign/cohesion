using System.Text;

namespace Assimalign.Cohesion.OpenApi.SourceGeneration;

/// <summary>
/// Helpers for emitting C# literal expressions for the generated metadata initializers.
/// </summary>
internal static class Literals
{
    internal const string MetadataNamespace = "global::Assimalign.Cohesion.OpenApi.Attributes";
    internal const string ModelNamespace = "global::Assimalign.Cohesion.OpenApi";

    /// <summary>Emits a C# string literal, or <c>null</c> for a null value.</summary>
    /// <param name="value">The string value.</param>
    /// <returns>A quoted, escaped literal or the token <c>null</c>.</returns>
    internal static string String(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Emits a C# boolean literal.</summary>
    /// <param name="value">The boolean value.</param>
    /// <returns><c>true</c> or <c>false</c>.</returns>
    internal static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Emits a fully-qualified enum member reference.</summary>
    /// <param name="enumType">The unqualified enum type name, for example <c>OperationType</c>.</param>
    /// <param name="member">The enum member name.</param>
    /// <returns>A qualified reference, for example <c>global::Assimalign.Cohesion.OpenApi.OperationType.Get</c>.</returns>
    internal static string Enum(string enumType, string member) => $"{ModelNamespace}.{enumType}.{member}";
}
