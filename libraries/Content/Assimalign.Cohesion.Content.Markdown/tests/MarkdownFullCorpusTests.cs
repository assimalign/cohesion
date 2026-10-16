using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownFullCorpusTests
{
    private readonly ITestOutputHelper _output;

    public MarkdownFullCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> Examples()
    {
        foreach (var example in CommonMarkCorpus.ReadExamples())
        {
            yield return [example.Section, example.Number, example.Markdown, example.Html];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - CommonMark 0.31.2: retained HTML or explicit degradation")]
    [MemberData(nameof(Examples))]
    public void ToHtml_OfficialExample_MatchesContract(string section, int number, string markdown, string expected)
    {
        // Arrange. Exact comparison is the official runner's --no-normalize mode: no transformations.
        var degradations = CommonMarkCorpus.ReadDegradations();

        // Act
        var actual = MarkdownText.ToHtml(markdown);

        // Assert
        if (degradations.TryGetValue(number, out var degradation))
        {
            degradation.Section.ShouldBe(section);
            if (!degradation.MatchesSpec)
            {
                actual.ShouldNotBe(expected, $"Example {number} now conforms: move the section into the retained set "
                    + "(or explicitly revise its mixed-section exclusions) before accepting the scope change.");
            }

            actual.ShouldBe(degradation.Html, $"{section} #{number}: {degradation.Reason}");
        }
        else
        {
            CommonMarkCorpus.NonRetainedSections.Contains(section).ShouldBeFalse("Every non-retained example needs a pinned contract.");
            actual.ShouldBe(expected, $"Retained {section} #{number}");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - CommonMark 0.31.2: complete inventory and section counts")]
    public void Corpus_AllSections_ReportsCompleteInventory()
    {
        // Arrange
        var degradations = CommonMarkCorpus.ReadDegradations();
        var counts = new Dictionary<string, (int Total, int Retained, int Conforming, int Degraded)>();
        foreach (var section in CommonMarkCorpus.Sections)
        {
            counts.Add(section, default);
        }

        // Act
        var next = 1;
        var exclusions = 0;
        foreach (var example in CommonMarkCorpus.ReadExamples())
        {
            example.Number.ShouldBe(next++);
            counts.ContainsKey(example.Section).ShouldBeTrue("Classify new sections explicitly.");
            var count = counts[example.Section];
            count.Total++;
            if (MarkdownText.ToHtml(example.Markdown) == example.Html)
            {
                count.Conforming++;
            }

            if (degradations.TryGetValue(example.Number, out var degradation))
            {
                degradation.Reason.ShouldNotBeNullOrWhiteSpace();
                degradation.MatchesSpec.ShouldBe(degradation.Html == example.Html);
                count.Degraded++;
                exclusions++;
            }
            else
            {
                count.Retained++;
            }

            counts[example.Section] = count;
        }

        // Assert
        foreach (var (section, count) in counts)
        {
            count.Total.ShouldBeGreaterThan(0);
            _output.WriteLine($"{section}: total={count.Total}, retained={count.Retained}, degradation={count.Degraded}, spec-equal={count.Conforming}");
            if (CommonMarkCorpus.NonRetainedSections.Contains(section) && count.Conforming == count.Total)
            {
                count.Conforming.ShouldNotBe(count.Total, $"{section} now passes: move the section into the retained set.");
            }
        }

        next.ShouldBe(653);
        exclusions.ShouldBe(degradations.Count, "No stale or unexecuted exclusions.");
        var hash = SHA256.HashData(File.ReadAllBytes(CommonMarkCorpus.PathFor("spec-0.31.2.json")));
        System.Convert.ToHexString(hash).ShouldBe("D431B29D97B6F73E69D547109CF5081578FAC931E72AFE95639EBE766C1B2A20");
    }
}
