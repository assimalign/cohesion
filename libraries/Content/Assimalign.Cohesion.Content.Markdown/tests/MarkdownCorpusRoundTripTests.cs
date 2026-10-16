using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownCorpusRoundTripTests
{
    public static IEnumerable<object[]> Examples()
    {
        foreach (var example in CommonMarkCorpus.ReadExamples())
        {
            yield return [example.Section, example.Number, example.Markdown];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - CommonMark 0.31.2: canonical round trip preserves every model field")]
    [MemberData(nameof(Examples))]
    public void Write_OfficialExample_PreservesModel(string section, int number, string markdown)
    {
        // Arrange. Includes every retained section, plus degradation trees as extra coverage.
        var expected = MarkdownText.Parse(markdown);

        // Act
        var written = MarkdownText.Write(expected);
        var actual = MarkdownText.Parse(written);

        // Assert
        var context = $"{section} #{number}";
        MarkdownModelComparison.ShouldEqual(expected, actual, context);
        MarkdownText.ToHtml(actual).ShouldBe(MarkdownText.ToHtml(expected), context);
        MarkdownText.Write(actual).ShouldBe(written, context);
    }
}
