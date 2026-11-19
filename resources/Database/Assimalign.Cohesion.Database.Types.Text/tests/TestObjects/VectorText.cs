using System.Globalization;
using System.Linq;

namespace Assimalign.Cohesion.Database.Types.Text.Tests;

internal static class VectorText
{
    // Test-only oracle notation. Production canonical rendering is outside this library.
    internal static string Format(TextVector vector) => vector.Lexemes.Count == 0 ? "{}" : string.Join(" ", vector.Lexemes.Select(lexeme =>
        "'" + lexeme.Text.Replace("'", "''", System.StringComparison.Ordinal) + "'"
        + (lexeme.Positions.Count == 0 ? "" : ":" + string.Join(",", lexeme.Positions.Select(position =>
            position.Position.ToString(CultureInfo.InvariantCulture) + (position.Weight == TextWeight.D ? "" : position.Weight.ToString()))))));
}
