using System;
using System.Globalization;
using System.IO;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Writes deterministic, indented inspections of BMFF and profile trees without reading payloads.</summary>
/// <remarks>Offsets and sizes describe the original header; descriptions show current fields.
/// Unloaded boxes are marked explicitly and are not measured through their writers. Control characters
/// in FourCCs and descriptions are escaped. Output uses invariant numbers and two spaces per depth.
/// Custom description hooks must not perform payload I/O or mutate the tree.</remarks>
public static class BmffTreeFormatter
{
    /// <summary>Writes a box and its descendants to a borrowed writer.</summary>
    /// <param name="box">The root box.</param>
    /// <param name="writer">The output, left open.</param>
    /// <param name="maxDepth">Maximum inclusive depth, starting at zero.</param>
    /// <exception cref="ArgumentNullException">The box or writer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
    /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
    /// <exception cref="IOException">Writing fails.</exception>
    /// <exception cref="ObjectDisposedException">The output is disposed.</exception>
    public static void Write(BmffBox box, TextWriter writer, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var description = new BmffInspectionWriter(writer);
        BmffBoxWalker.Walk(box, (node, depth, _) => WriteLine(node, depth, writer, description), maxDepth: maxDepth);
    }

    /// <summary>Writes every box in a document to a borrowed writer.</summary>
    /// <param name="document">The document.</param>
    /// <param name="writer">The output, left open.</param>
    /// <param name="maxDepth">Maximum inclusive depth, with top-level boxes at zero.</param>
    /// <exception cref="ArgumentNullException">The document or writer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
    /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
    /// <exception cref="IOException">Writing fails.</exception>
    /// <exception cref="ObjectDisposedException">The output is disposed.</exception>
    public static void Write(BmffDocument document, TextWriter writer, int maxDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var description = new BmffInspectionWriter(writer);
        BmffBoxWalker.Walk(document, (node, depth, _) => WriteLine(node, depth, writer, description), maxDepth: maxDepth);
    }

    /// <summary>Formats a box and its descendants with LF line endings.</summary>
    /// <param name="box">The root box.</param>
    /// <param name="maxDepth">Maximum inclusive depth, starting at zero.</param>
    /// <returns>The tree text, ending in a newline.</returns>
    /// <exception cref="ArgumentNullException">The box is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
    /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
    public static string Format(BmffBox box, int maxDepth = 64)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        Write(box, writer, maxDepth);
        return writer.ToString();
    }

    /// <summary>Formats a document with LF line endings.</summary>
    /// <param name="document">The document.</param>
    /// <param name="maxDepth">Maximum inclusive depth, with top-level boxes at zero.</param>
    /// <returns>The tree text, or an empty string for an empty document.</returns>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The depth limit is negative.</exception>
    /// <exception cref="ContentFormatException">A visited node is null, cyclic or too deep.</exception>
    public static string Format(BmffDocument document, int maxDepth = 64)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        Write(document, writer, maxDepth);
        return writer.ToString();
    }

    private static BmffWalkAction WriteLine(BmffBox box, int depth, TextWriter writer, TextWriter description)
    {
        for (var index = 0; index < depth; index++) { writer.Write("  "); }
        description.Write(box.BoxType.ToFourCC().ToString());
        writer.Write(FormattableString.Invariant($" @0x{box.Offset:X} size="));
        writer.Write(box.Header.TotalSize?.ToString(CultureInfo.InvariantCulture) ??
            (box.Header.HeaderSize == 0 && box.Limit != 0 ? box.Limit.ToString(CultureInfo.InvariantCulture) : "?"));
        if (box.Header.HeaderSize == 0) { writer.Write(" [unloaded]"); }
        if (box.Header.HeaderSize == (box.Header.ExtendedType.HasValue ? 32 : 16)) { writer.Write(" [large-size]"); }
        if (box.Header.ExtendsToEnd) { writer.Write(" [size-zero]"); }
        if (box.Header.ExtendedType is { } uuid) { writer.Write($" uuid={uuid:D}"); }
        if (box is BmffBoxComposite composite) { writer.Write(FormattableString.Invariant($" children={composite.Children.Count}")); }
        if (box is UnknownBox unknown) { writer.Write($" [unknown preserved:{unknown.PreservationReason}]"); }
        var payloadLength = BmffInspection.GetLazyLength(box);
        if (payloadLength.HasValue) { writer.Write(FormattableString.Invariant($" lazy-payload={payloadLength.Value}")); }
        box.WriteDescription(description);
        writer.WriteLine();
        return BmffWalkAction.Continue;
    }
}
