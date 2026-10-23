using System;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

/// <summary>Maps a relocation rule's checkpoint coordinates into one candidate output layout.</summary>
/// <remarks>Used only during a registered rule's apply callback. Never accumulate deltas between callbacks.</remarks>
[global::System.Diagnostics.DebuggerDisplay("BmffRelocationContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BmffRelocationContext : IBmffRelocationContext
{
    private readonly BmffLayout _original;
    private readonly BmffLayout _output;
    internal BmffRelocationContext(BmffLayout original, BmffLayout output) { _original = original; _output = output; }

    /// <summary>Maps a byte address in a retained box, including an opaque payload.</summary>
    /// <param name="address">An absolute byte address in the checkpoint layout.</param>
    /// <returns>The address in the candidate output.</returns>
    /// <exception cref="ContentFormatException">The target is absent, shortened or outside the known layout.</exception>
    public ulong MapOffset(ulong address) => _original.MapAddress(address, _output);

    /// <summary>Gets a box's header position in the last successful checkpoint.</summary>
    /// <param name="box">A box present in that checkpoint.</param>
    /// <returns>Its absolute header offset.</returns>
    /// <exception cref="ArgumentNullException">The box is null.</exception>
    /// <exception cref="ContentFormatException">The box is absent from the checkpoint.</exception>
    public long GetOriginalOffset(BmffBox box) => GetOffset(_original, box);

    /// <summary>Gets a box's header position in the candidate output.</summary>
    /// <param name="box">A retained box.</param>
    /// <returns>Its absolute header offset.</returns>
    /// <exception cref="ArgumentNullException">The box is null.</exception>
    /// <exception cref="ContentFormatException">The box is absent from the output.</exception>
    public long GetOutputOffset(BmffBox box) => GetOffset(_output, box);

    private static long GetOffset(BmffLayout layout, BmffBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return layout.Entries.TryGetValue(box, out var entry) ? entry.Offset :
            throw new ContentFormatException(ErrorMessages.RelocationTarget, ContentFormatErrorCode.InvalidStructure, box.Offset);
    }

    private sealed class DebugView
    {
        public DebugView(BmffRelocationContext value)
        {
        }
    }
}
