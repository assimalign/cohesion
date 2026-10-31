using System;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal static class SqlCopyEncoder
{
    internal static void Append(StringBuilder output, string? value, SqlCopyOptions options, bool forceQuote, bool singleColumn)
    {
        if (value is null) { output.Append(options.NullString); return; }
        // PostgreSQL commands/copyto.c:1443-1590 escapes control characters, delimiter and
        // backslash; :1596-1708 quotes NULL lookalikes and the single-column \\. terminator.
        if (!options.IsCsv)
        {
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    case '\v': output.Append("\\v"); break;
                    case '\\': output.Append("\\\\"); break;
                    default:
                        if (character == options.Delimiter) { output.Append('\\'); }
                        output.Append(character);
                        break;
                }
            }
            return;
        }
        bool quote = forceQuote || value == options.NullString || singleColumn && value == "\\." ||
            value.Contains(options.Delimiter) || value.Contains(options.Quote) || value.Contains('\r') || value.Contains('\n');
        if (!quote) { output.Append(value); return; }
        output.Append(options.Quote);
        foreach (char character in value)
        {
            if (character == options.Quote || character == options.Escape) { output.Append(options.Escape); }
            output.Append(character);
        }
        output.Append(options.Quote);
    }
}
