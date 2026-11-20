using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Checks the compliance matrix (#431) in <see cref="AmqpComplianceMatrix"/>: every map's rules cite the Parts of
/// AMQP 1.0 they come from and name the tests or corpus cases that pin them, and every Part has rules. Reports
/// each Part's rules, evidence, leniencies and gaps to the test output.
/// </summary>
public class AmqpComplianceMatrixTests
{
    private static readonly (int Part, string Title)[] _parts =
    [
        (1, "Types"),
        (2, "Transport"),
        (3, "Messaging"),
        (4, "Transactions"),
        (5, "Security")
    ];

    private readonly ITestOutputHelper _output;

    public AmqpComplianceMatrixTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadParts: Should read the Parts a reference cites, in the order it cites them")]
    [InlineData("Part 2, section 2.7.5", "2")]
    [InlineData("Part 2, section 2.6", "2")]
    [InlineData("Part 3, sections 3.2.2, 3.2.3, 3.2.9 and 3.2.10", "3")]
    [InlineData("Part 3, sections 3.5.9 and 3.5.10 to 3.5.13", "3")]
    [InlineData("Part 2, sections 2.6.12 and 2.7.6; Part 3, sections 3.4.2 to 3.4.5", "2,3")]
    [InlineData("Part 3, section 3.4.6, and Part 2, section 2.6.14", "3,2")]
    [InlineData("Part 1, section 1.1.4; Part 2, section 2.8.2; Part 1, section 1.4", "1,2")]
    public void ReadParts_WithCitations_ShouldReadTheirParts(string reference, string expected)
    {
        // Act
        IReadOnlyList<int>? parts = AmqpComplianceMatrix.ReadParts(reference);

        // Assert
        parts.ShouldNotBeNull().ShouldBe(expected.Split(',').Select(int.Parse));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ReadParts: Should refuse a citation that does not name a Part and sections within it")]
    [InlineData("section 2.7.5")]
    [InlineData("Part 2 section 2.7.5")]
    [InlineData("Part 6, section 6.1")]
    [InlineData("Part 2, section 3.4.1")]
    [InlineData("Part 2, sections 2.7.5")]
    [InlineData("Part 2, section 2.7.5 and 2.7.6")]
    [InlineData("Part 2, sections 2.6.12 and 3.4.2")]
    [InlineData("Part 2, section 2.7.5; section 2.7.6")]
    public void ReadParts_WithMalformedCitation_ShouldReturnNull(string reference)
    {
        // Act
        IReadOnlyList<int>? parts = AmqpComplianceMatrix.ReadParts(reference);

        // Assert
        parts.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Matrix: Should hold every map, each rule unique in its map and citing the Parts it comes from")]
    public void Matrix_OnEveryMap_ShouldHoldUniqueRulesThatCiteTheSpecification()
    {
        // Arrange
        IReadOnlyList<AmqpComplianceEntry> entries = AmqpComplianceMatrix.Entries;
        AmqpComplianceMap[] maps = [.. Enum.GetValues<AmqpComplianceMap>().Where(map => map != AmqpComplianceMap.None)];

        // Act
        AmqpComplianceMap[] missing = [.. maps.Where(map => !entries.Any(entry => entry.Map == map))];
        string[] duplicates =
        [
            .. entries
                .GroupBy(entry => (entry.Map, entry.Id))
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key.Map} {group.Key.Id}")
        ];
        string[] malformed =
        [
            .. entries
                .Where(entry => AmqpComplianceMatrix.ReadParts(entry.Reference) is null)
                .Select(entry => $"{entry.Map} {entry.Id}: {entry.Reference}")
        ];

        // Assert
        missing.ShouldBeEmpty();
        duplicates.ShouldBeEmpty();
        malformed.ShouldBeEmpty();
        entries.ShouldAllBe(entry => !string.IsNullOrWhiteSpace(entry.Statement));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Matrix: Should name for each rule what it allows or refuses, and for each map both")]
    public void Matrix_OnEveryRule_ShouldNameItsEvidence()
    {
        // Arrange
        IReadOnlyList<AmqpComplianceEntry> entries = AmqpComplianceMatrix.Entries;

        // Act
        string[] unpinned = [.. entries.Where(entry => entry.Accepted.Count + entry.Refused.Count == 0).Select(Name)];
        string[] forbidsWithoutRefusal = [.. entries.Where(entry => entry.Forbids && entry.Refused.Count == 0).Select(Name)];
        string[] lenientWithoutAcceptance = [.. entries.Where(entry => entry.Leniency && entry.Accepted.Count == 0).Select(Name)];
        string[] repeated =
        [
            .. entries
                .Where(entry => entry.Accepted.Concat(entry.Refused).Distinct().Count() != entry.Accepted.Count + entry.Refused.Count)
                .Select(Name)
        ];
        string[] oneSided =
        [
            .. entries
                .GroupBy(entry => entry.Map)
                .Where(map => !map.Any(entry => entry.Accepted.Count > 0) || !map.Any(entry => entry.Refused.Count > 0))
                .Select(map => map.Key.ToString())
        ];

        // Assert
        unpinned.ShouldBeEmpty();
        forbidsWithoutRefusal.ShouldBeEmpty();
        lenientWithoutAcceptance.ShouldBeEmpty();
        repeated.ShouldBeEmpty();
        oneSided.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Matrix: Should cite for each gap the Parts it comes from and the work item that tracks it")]
    public void Matrix_OnEveryGap_ShouldCiteTheSpecificationAndItsWorkItem()
    {
        // Arrange
        IReadOnlyList<AmqpComplianceGap> gaps = AmqpComplianceMatrix.Gaps;
        Regex workItem = new("^#[1-9][0-9]*$", RegexOptions.CultureInvariant);

        // Act
        string[] identifiers = [.. gaps.Select(gap => gap.Id)];
        string[] malformed = [.. gaps.Where(gap => AmqpComplianceMatrix.ReadParts(gap.Reference) is null).Select(gap => $"{gap.Id}: {gap.Reference}")];
        string[] untracked = [.. gaps.Where(gap => !workItem.IsMatch(gap.WorkItem)).Select(gap => $"{gap.Id}: {gap.WorkItem}")];

        // Assert
        identifiers.Distinct().Count().ShouldBe(identifiers.Length);
        malformed.ShouldBeEmpty();
        untracked.ShouldBeEmpty();
        gaps.ShouldAllBe(gap => !string.IsNullOrWhiteSpace(gap.Statement));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Matrix: Should hold rules from every Part, reporting each Part's rules, evidence, leniencies and gaps")]
    public void Matrix_OnEachPart_ShouldReportItsRulesAndGaps()
    {
        // Arrange
        IReadOnlyList<AmqpComplianceEntry> entries = AmqpComplianceMatrix.Entries;
        IReadOnlyList<AmqpComplianceGap> gaps = AmqpComplianceMatrix.Gaps;
        Dictionary<int, int> rules = [];

        // Act
        _output.WriteLine($"{Count(entries.Count, "rule")} in {Count(entries.Select(entry => entry.Map).Distinct().Count(), "map")}; {Count(gaps.Count, "gap")}.");

        foreach ((int part, string title) in _parts)
        {
            AmqpComplianceEntry[] cited = [.. entries.Where(entry => entry.Parts.Contains(part))];
            AmqpComplianceEntry[] lenient = [.. cited.Where(entry => entry.Leniency)];
            AmqpComplianceGap[] open = [.. gaps.Where(gap => gap.Parts.Contains(part))];

            rules[part] = cited.Length;
            _output.WriteLine(string.Empty);
            _output.WriteLine(
                $"Part {part}, {title}: {Count(cited.Length, "rule")} in {Count(cited.Select(entry => entry.Map).Distinct().Count(), "map")}, " +
                $"{Count(lenient.Length, "leniency", "leniencies")}, {Count(open.Length, "gap")}.");

            foreach (IGrouping<AmqpComplianceMap, AmqpComplianceEntry> map in cited.GroupBy(entry => entry.Map))
            {
                string evidence = AmqpComplianceMatrix.NamesCases(map.Key) ? "cases" : "tests";

                _output.WriteLine(
                    $"  {map.Key}: {string.Join(", ", map.Select(entry => entry.Id))}; " +
                    $"{map.Sum(entry => entry.Accepted.Count)} accepting and {map.Sum(entry => entry.Refused.Count)} refusing {evidence}.");
            }

            if (lenient.Length > 0)
            {
                _output.WriteLine($"  Leniencies: {string.Join(", ", lenient.Select(Name))}.");
            }

            foreach (AmqpComplianceGap gap in open)
            {
                _output.WriteLine($"  Gap {gap.Id} ({gap.Reference}; {gap.WorkItem}): {gap.Statement}");
            }
        }

        // Assert
        rules.Where(part => part.Value == 0).Select(part => $"Part {part.Key}").ShouldBeEmpty();
    }

    private static string Name(AmqpComplianceEntry entry)
    {
        return $"{entry.Map} {entry.Id}";
    }

    private static string Count(int count, string noun, string? plural = null)
    {
        return count == 1 ? $"1 {noun}" : $"{count} {plural ?? noun + "s"}";
    }
}
