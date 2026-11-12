using System.Globalization;
using System.Linq;

namespace Assimalign.Cohesion.Database.Text.Tests;

internal static class VectorText
{
    // Test-only oracle notation. Production canonical rendering belongs to IX-T2.
    internal static string Format(TextVector vector) => string.Join(" ", vector.Lexemes.Select(lexeme =>
        "'" + lexeme.Text.Replace("'", "''", System.StringComparison.Ordinal) + "'"
        + (lexeme.Positions.Count == 0 ? "" : ":" + string.Join(",", lexeme.Positions.Select(position =>
            position.Position.ToString(CultureInfo.InvariantCulture) + (position.Weight == TextWeight.D ? "" : position.Weight.ToString()))))));
}
