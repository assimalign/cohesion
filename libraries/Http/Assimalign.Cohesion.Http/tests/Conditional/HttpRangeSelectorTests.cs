using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 14.2 compliance tests for <see cref="HttpRangeSelector"/>: satisfiable ranges
/// produce <c>206</c> slices with correct <c>Content-Range</c> values, wholly-unsatisfiable requests
/// produce <c>416</c> with <c>bytes */N</c>, and an oversized range set is ignored in favor of a full
/// <c>200</c>.
/// </summary>
public class HttpRangeSelectorTests
{
    [Fact]
    public void Select_SingleSatisfiableRange_ShouldReturnOneSlice()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=0-499");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Partial);
        selection.IsSingleSlice.ShouldBeTrue();
        selection.Slices[0].Offset.ShouldBe(0);
        selection.Slices[0].Length.ShouldBe(500);
        selection.Slices[0].ContentRange.ToString().ShouldBe("bytes 0-499/1000");
    }

    [Fact]
    public void Select_SuffixRange_ShouldReturnTrailingSlice()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=-200");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Partial);
        selection.Slices[0].Offset.ShouldBe(800);
        selection.Slices[0].Length.ShouldBe(200);
        selection.Slices[0].ContentRange.ToString().ShouldBe("bytes 800-999/1000");
    }

    [Fact]
    public void Select_MultipleSatisfiableRanges_ShouldReturnSlicesInOrder()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=0-99,500-599,-100");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Partial);
        selection.Slices.Count.ShouldBe(3);
        selection.Slices[0].ContentRange.ToString().ShouldBe("bytes 0-99/1000");
        selection.Slices[1].ContentRange.ToString().ShouldBe("bytes 500-599/1000");
        selection.Slices[2].ContentRange.ToString().ShouldBe("bytes 900-999/1000");
    }

    [Fact]
    public void Select_MixedSatisfiability_ShouldKeepOnlySatisfiableRanges()
    {
        // The second range starts past the end and is dropped; the first survives.
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=0-99,2000-2999");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Partial);
        selection.Slices.Count.ShouldBe(1);
        selection.Slices[0].Offset.ShouldBe(0);
    }

    [Fact]
    public void Select_AllUnsatisfiable_ShouldReturn416WithStarForm()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=2000-2999");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Unsatisfiable);
        selection.UnsatisfiedContentRange.ToString().ShouldBe("bytes */1000");
    }

    [Fact]
    public void Select_ZeroSuffixAgainstContent_ShouldBeUnsatisfiable()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=-0");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Unsatisfiable);
        selection.UnsatisfiedContentRange.ToString().ShouldBe("bytes */1000");
    }

    [Fact]
    public void Select_EmptyRepresentation_ShouldBeUnsatisfiable()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=0-0");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 0);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Unsatisfiable);
        selection.UnsatisfiedContentRange.ToString().ShouldBe("bytes */0");
    }

    [Fact]
    public void Select_TooManyRanges_ShouldFallBackToFull()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=0-0,1-1,2-2,3-3");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000, maxRanges: 3);

        selection.Status.ShouldBe(HttpRangeSelectionStatus.Full);
        selection.Slices.ShouldBeEmpty();
    }

    [Fact]
    public void Select_OpenEndedRange_ShouldSpanToEnd()
    {
        HttpRangeHeader header = HttpRangeHeader.Parse("bytes=990-");

        HttpRangeSelection selection = HttpRangeSelector.Select(header, 1000);

        selection.Slices[0].Offset.ShouldBe(990);
        selection.Slices[0].Length.ShouldBe(10);
        selection.Slices[0].ContentRange.ToString().ShouldBe("bytes 990-999/1000");
    }
}
