using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The typed result of <see cref="HttpRangeSelector.Select(in HttpRangeHeader, long, int)"/>: the
/// response disposition for a range request (RFC 9110 &#167; 14.2) together with the data needed to
/// build that response — the selected <see cref="Slices"/> for a <c>206</c>, or the
/// <see cref="UnsatisfiedContentRange"/> for a <c>416</c>.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpRangeSelection
{
    private readonly HttpRangeSlice[]? slices;

    private HttpRangeSelection(HttpRangeSelectionStatus status, HttpRangeSlice[]? slices, HttpContentRange unsatisfiedContentRange, long completeLength)
    {
        Status = status;
        this.slices = slices;
        UnsatisfiedContentRange = unsatisfiedContentRange;
        CompleteLength = completeLength;
    }

    /// <summary>Gets the response disposition for the range request.</summary>
    public HttpRangeSelectionStatus Status { get; }

    /// <summary>Gets the total length of the representation the selection was computed against.</summary>
    public long CompleteLength { get; }

    /// <summary>
    /// Gets the selected slices when <see cref="Status"/> is
    /// <see cref="HttpRangeSelectionStatus.Partial"/> (in request order); empty otherwise.
    /// </summary>
    public IReadOnlyList<HttpRangeSlice> Slices
        => slices ?? (IReadOnlyList<HttpRangeSlice>)Array.Empty<HttpRangeSlice>();

    /// <summary>
    /// Gets the <c>bytes */N</c> content-range to send with a <c>416</c> when <see cref="Status"/> is
    /// <see cref="HttpRangeSelectionStatus.Unsatisfiable"/>; a default value otherwise.
    /// </summary>
    public HttpContentRange UnsatisfiedContentRange { get; }

    /// <summary>Gets a value indicating whether the selection is a single satisfiable slice.</summary>
    public bool IsSingleSlice => Status == HttpRangeSelectionStatus.Partial && slices is { Length: 1 };

    private string DebuggerDisplay => Status switch
    {
        HttpRangeSelectionStatus.Partial => $"Partial ({slices?.Length ?? 0} slice(s))",
        HttpRangeSelectionStatus.Unsatisfiable => $"Unsatisfiable ({UnsatisfiedContentRange})",
        _ => "Full",
    };

    internal static HttpRangeSelection Full(long completeLength)
        => new(HttpRangeSelectionStatus.Full, null, default, completeLength);

    internal static HttpRangeSelection Partial(HttpRangeSlice[] slices, long completeLength)
        => new(HttpRangeSelectionStatus.Partial, slices, default, completeLength);

    internal static HttpRangeSelection Unsatisfiable(long completeLength)
        => new(HttpRangeSelectionStatus.Unsatisfiable, null, HttpContentRange.Unsatisfied(completeLength), completeLength);
}
