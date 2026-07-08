using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Applies a parsed <c>Range</c> header to a representation of known length per RFC 9110
/// &#167; 14.2, deciding between a <c>206 Partial Content</c> response (returning the concrete byte
/// slices) and a <c>416 Range Not Satisfiable</c> response (returning the <c>bytes */N</c>
/// content-range). This is the selection step only; whether a present range should be honored at all
/// is a precondition/<c>If-Range</c> decision made by <see cref="HttpPreconditionEvaluator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ranges are resolved in the order the client listed them and are <em>not</em> coalesced or
/// reordered (RFC 9110 &#167; 14.2 permits coalescing but does not require it; preserving client
/// order keeps the primitive predictable). A range set larger than <paramref name="maxRanges"/> is
/// treated as not worth honoring and yields <see cref="HttpRangeSelectionStatus.Full"/> — the
/// denial-of-service mitigation the RFC explicitly allows for "egregious" range requests.
/// </para>
/// </remarks>
public static class HttpRangeSelector
{
    /// <summary>
    /// The default upper bound on the number of ranges honored in a single request before the whole
    /// range set is ignored in favor of a full <c>200</c> response.
    /// </summary>
    public const int DefaultMaxRanges = 16;

    /// <summary>
    /// Selects the response for <paramref name="range"/> against a representation of
    /// <paramref name="completeLength"/> bytes (RFC 9110 &#167; 14.2).
    /// </summary>
    /// <param name="range">The parsed <c>Range</c> header.</param>
    /// <param name="completeLength">The total length, in bytes, of the selected representation.</param>
    /// <param name="maxRanges">The maximum number of ranges to honor before serving the full representation instead; defaults to <see cref="DefaultMaxRanges"/>.</param>
    /// <returns>
    /// A <see cref="HttpRangeSelection"/> describing whether to serve <c>200</c> (full), <c>206</c>
    /// (partial, with slices), or <c>416</c> (unsatisfiable, with a <c>bytes */N</c> content-range).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeLength"/> is negative or <paramref name="maxRanges"/> is less than one.</exception>
    public static HttpRangeSelection Select(in HttpRangeHeader range, long completeLength, int maxRanges = DefaultMaxRanges)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completeLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRanges, 1);

        // A parsed-but-empty header (or one carrying no usable unit) means "no range" → serve full.
        if (range.IsEmpty || range.Count == 0)
        {
            return HttpRangeSelection.Full(completeLength);
        }

        // Denial-of-service guard: an egregiously large range set is ignored (RFC 9110 § 14.2).
        if (range.Count > maxRanges)
        {
            return HttpRangeSelection.Full(completeLength);
        }

        IReadOnlyList<HttpRange> ranges = range.Ranges;
        List<HttpRangeSlice>? slices = null;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].TryResolve(completeLength, out long offset, out long length))
            {
                (slices ??= new List<HttpRangeSlice>(ranges.Count)).Add(new HttpRangeSlice(offset, length, completeLength));
            }
        }

        // RFC 9110 § 14.2: if none of the ranges overlap the representation, the request is
        // unsatisfiable → 416 with "bytes */complete-length".
        if (slices is null)
        {
            return HttpRangeSelection.Unsatisfiable(completeLength);
        }

        return HttpRangeSelection.Partial(slices.ToArray(), completeLength);
    }
}
