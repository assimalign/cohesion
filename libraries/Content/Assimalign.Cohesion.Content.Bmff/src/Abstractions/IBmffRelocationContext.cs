using System;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Maps a relocation rule's checkpoint coordinates into one candidate output layout.</summary>
/// <remarks>Used only during a registered rule's apply callback. Never accumulate deltas between callbacks.</remarks>
public interface IBmffRelocationContext
{
    /// <summary>Maps a byte address in a retained box, including an opaque payload.</summary>
    /// <param name="address">An absolute byte address in the checkpoint layout.</param>
    /// <returns>The address in the candidate output.</returns>
    /// <exception cref="ContentFormatException">The target is absent, shortened or outside the known layout.</exception>
    ulong MapOffset(ulong address);

    /// <summary>Gets a box's header position in the last successful checkpoint.</summary>
    /// <param name="box">A box present in that checkpoint.</param>
    /// <returns>Its absolute header offset.</returns>
    /// <exception cref="ArgumentNullException">The box is null.</exception>
    /// <exception cref="ContentFormatException">The box is absent from the checkpoint.</exception>
    long GetOriginalOffset(BmffBox box);

    /// <summary>Gets a box's header position in the candidate output.</summary>
    /// <param name="box">A retained box.</param>
    /// <returns>Its absolute header offset.</returns>
    /// <exception cref="ArgumentNullException">The box is null.</exception>
    /// <exception cref="ContentFormatException">The box is absent from the output.</exception>
    long GetOutputOffset(BmffBox box);
}
