using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Materializes engine/client results and diagnostics into display types.</summary>
internal static class QueryResultReader
{
    /// <summary>Copies an in-process <see cref="QueryResult"/> into an outcome (status, count, diagnostics, rows).</summary>
    public static async Task FillAsync(StatementOutcome outcome, QueryResult result, CancellationToken cancellationToken)
    {
        outcome.Status = result.Status.ToString();
        outcome.Failed = result.Status != QueryResultStatus.Success;
        outcome.AffectedCount = result.AffectedCount;

        if (result.Diagnostics is { } diagnostics)
        {
            foreach (Diagnostic diagnostic in diagnostics)
            {
                outcome.Diagnostics.Add(ToInfo("engine", diagnostic, outcome.StatementOffset, null));
            }
        }

        if (result is QueryResultSet set)
        {
            await using (set.ConfigureAwait(false))
            {
                var table = new TabularResult
                {
                    Columns = [.. set.Columns.Select(column => column.Name)],
                    ColumnTypes = [.. set.Columns.Select(column => $"{column.Type}{(column.IsNullable ? "?" : string.Empty)}")],
                };

                await foreach (QueryRow row in set.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (table.Rows.Count >= StatementOutcome.MaxRows)
                    {
                        table.Truncated = true;
                        break;
                    }

                    var values = new string[row.FieldCount];
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = row.IsNull(i) ? ValueFormatter.NullText : ValueFormatter.Format(row.GetValue(i));
                    }

                    table.Rows.Add(values);
                }

                outcome.Table = table;
            }
        }
    }

    /// <summary>Builds a table from client-side column names/types and boxed row values.</summary>
    public static TabularResult ToTable(IEnumerable<(string Name, string Type)> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var columnList = columns.ToList();
        var table = new TabularResult
        {
            Columns = [.. columnList.Select(column => column.Name)],
            ColumnTypes = [.. columnList.Select(column => column.Type)],
        };

        foreach (IReadOnlyList<object?> row in rows)
        {
            if (table.Rows.Count >= StatementOutcome.MaxRows)
            {
                table.Truncated = true;
                break;
            }

            var values = new string[row.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = ValueFormatter.Format(row[i]);
            }

            table.Rows.Add(values);
        }

        return table;
    }

    public static DiagnosticInfo ToInfo(string source, Diagnostic diagnostic, int statementOffset, string? editorText)
    {
        int? absolute = diagnostic.Start is { } start ? statementOffset + start : null;
        int? length = diagnostic.Length is { } value and >= 0 ? value : null;
        string position = absolute is { } at && editorText is not null ? LineColumn(editorText, at) : string.Empty;
        return new DiagnosticInfo(
            source,
            diagnostic.Severity?.ToString() ?? "None",
            diagnostic.Code ?? "(no code)",
            diagnostic.Message ?? string.Empty,
            absolute,
            length,
            position);
    }

    public static string LineColumn(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1;
        int column = 1;
        for (int i = 0; i < offset; i++)
        {
            // "\r\n", a lone '\r' (WinUI TextBox), and '\n' each end one line.
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                line++;
                column = 1;
            }
            else if (text[i] != '\r')
            {
                column++;
            }
        }

        return $"line {line}, col {column} (offset {offset})";
    }
}
