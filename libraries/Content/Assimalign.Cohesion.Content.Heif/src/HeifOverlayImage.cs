using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>An iovl canvas and ordered image placements; no pixel composition occurs.</summary>
public sealed class HeifOverlayImage
{
    internal HeifOverlayImage(uint width, uint height, IReadOnlyList<ushort> fill, IReadOnlyList<Layer> layers)
    { Width = width; Height = height; FillValues = fill; Layers = layers; }
    /// <summary>Gets the canvas width.</summary>
    public uint Width { get; }
    /// <summary>Gets the canvas height.</summary>
    public uint Height { get; }
    /// <summary>Gets the four 16-bit canvas fill values in RGBA order.</summary>
    public IReadOnlyList<ushort> FillValues { get; }
    /// <summary>Gets placements in dimg order.</summary>
    public IReadOnlyList<Layer> Layers { get; }
    /// <summary>One image placement on the overlay canvas.</summary>
    public readonly struct Layer
    {
        internal Layer(HeifItem item, int x, int y) { Item = item; HorizontalOffset = x; VerticalOffset = y; }
        /// <summary>Gets the input image.</summary>
        public HeifItem Item { get; }
        /// <summary>Gets the signed horizontal offset.</summary>
        public int HorizontalOffset { get; }
        /// <summary>Gets the signed vertical offset.</summary>
        public int VerticalOffset { get; }
    }
}
