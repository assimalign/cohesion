using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>A grid derived image with row-major tile references; no pixel composition occurs.</summary>
public sealed class HeifGridImage
{
    internal HeifGridImage(int rows, int columns, uint width, uint height, IReadOnlyList<HeifItem> tiles)
    { Rows = rows; Columns = columns; Width = width; Height = height; Tiles = tiles; }
    /// <summary>Gets the row count.</summary>
    public int Rows { get; }
    /// <summary>Gets the column count.</summary>
    public int Columns { get; }
    /// <summary>Gets the output width.</summary>
    public uint Width { get; }
    /// <summary>Gets the output height.</summary>
    public uint Height { get; }
    /// <summary>Gets tiles in dimg reference order.</summary>
    public IReadOnlyList<HeifItem> Tiles { get; }
}
