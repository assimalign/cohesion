using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>Describes a source span using the model parser's line and character positions.</summary>
public sealed record class Location
{
    Location(int startLine, int endLine, int start, int end)
    {
        EndLine = endLine;
        StartLine = startLine;
        Start = start;
        End = end;
    }

    /// <summary>
    /// The start position.
    /// </summary>
    public int Start { get; }

    /// <summary>
    /// The beginning line number of the node.
    /// </summary>
    public int StartLine { get; }

    /// <summary>
    /// The end position.
    /// </summary>
    public int End { get; }

    /// <summary>
    /// The ending line number of the node.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// Creates a location from the source span reported by a model parser.
    /// </summary>
    /// <param name="startLine">The line on which the span begins.</param>
    /// <param name="endLine">The line on which the span ends.</param>
    /// <param name="start">The span's starting character position.</param>
    /// <param name="end">The span's ending character position.</param>
    /// <returns>A location retaining the supplied positions without normalization.</returns>
    public static Location Create(int startLine, int endLine, int start, int end) =>
        new Location(startLine, endLine, start, end);
}
