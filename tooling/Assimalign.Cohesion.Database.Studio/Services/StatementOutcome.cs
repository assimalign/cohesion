using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>A materialized, display-ready result table (values already formatted).</summary>
internal sealed class TabularResult
{
    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<string> ColumnTypes { get; init; }

    public List<string[]> Rows { get; } = [];

    /// <summary>True when more rows existed than <see cref="StatementOutcome.MaxRows"/>.</summary>
    public bool Truncated { get; set; }

    public int IndexOf(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public string Get(string[] row, string column)
    {
        int index = IndexOf(column);
        return index < 0 || index >= row.Length ? string.Empty : row[index];
    }

    public string ToTsv()
    {
        var builder = new StringBuilder();
        builder.AppendJoin('\t', Columns).AppendLine();
        foreach (string[] row in Rows)
        {
            builder.AppendJoin('\t', row).AppendLine();
        }

        return builder.ToString();
    }
}

/// <summary>One diagnostic, with its position translated to the editor text.</summary>
internal sealed record DiagnosticInfo(
    string Source,
    string Severity,
    string Code,
    string Message,
    int? AbsoluteStart,
    int? Length,
    string Position);

/// <summary>What happened when one statement ran.</summary>
internal sealed class StatementOutcome
{
    public const int MaxRows = 10_000;

    public required string Statement { get; init; }

    /// <summary>Offset of <see cref="Statement"/> inside the editor text.</summary>
    public int StatementOffset { get; init; }

    public string Status { get; set; } = "Success";

    public bool Failed { get; set; }

    public bool Skipped { get; set; }

    public long AffectedCount { get; set; } = -1;

    public double ElapsedMs { get; set; }

    public TabularResult? Table { get; set; }

    public List<string>? Paths { get; set; }

    public List<DiagnosticInfo> Diagnostics { get; } = [];

    public string? Error { get; set; }

    /// <summary>Free-form note (for example which client call was used).</summary>
    public string? Note { get; set; }

    public string Preview
    {
        get
        {
            string flat = string.Join(' ', Statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= 80 ? flat : flat[..77] + "...";
        }
    }
}
