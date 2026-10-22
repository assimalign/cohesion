using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpMediaTypeEqualityTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - Media equality: Preserve each name's occurrence order symmetrically")]
    [InlineData("x=a; x=b", "x=a; x=b", true)]
    [InlineData("x=a; x=b", "x=b; x=a", false)]
    [InlineData("x=a; y=c; x=b", "y=c; x=a; x=b", true)]
    [InlineData("x=a; x=a", "x=a; x=b", false)]
    [InlineData("x=a; y=b", "x=a; x=a", false)]
    [InlineData("x=a", "x=a; x=a", false)]
    [InlineData("x=A; x=B", "X=a; X=b", true)]
    [InlineData("x=a; y=b", "y=B; X=A", true)]
    public void Equals_ParameterOccurrences_ShouldCompareSymmetrically(string leftText, string rightText, bool expected)
    {
        // Arrange
        HttpMediaType left = HttpMediaType.Parse("text/plain; " + leftText);
        HttpMediaType right = HttpMediaType.Parse("text/plain; " + rightText);
        HttpMediaType copy = left;

        // Act / Assert
        left.Equals(copy).ShouldBeTrue();
        left.Equals(right).ShouldBe(expected);
        right.Equals(left).ShouldBe(expected);
        (left == right).ShouldBe(expected);
        (left != right).ShouldBe(!expected);
        if (expected)
        {
            left.GetHashCode().ShouldBe(right.GetHashCode());
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Media equality: Transitive values work in dictionary and hash set lookups")]
    public void Equals_ReorderedNameGroups_ShouldSupportCollectionsAndTransitivity()
    {
        // Arrange
        HttpMediaType a = HttpMediaType.Parse("text/plain; x=a; y=c; x=b");
        HttpMediaType b = HttpMediaType.Parse("text/plain; y=C; X=A; x=B");
        HttpMediaType c = HttpMediaType.Parse("TEXT/PLAIN; x=A; x=B; y=c");
        var dictionary = new Dictionary<HttpMediaType, string> { [a] = "found" };
        var set = new HashSet<HttpMediaType> { a, b, c };

        // Act / Assert
        a.Equals(b).ShouldBeTrue();
        b.Equals(c).ShouldBeTrue();
        a.Equals(c).ShouldBeTrue();
        dictionary[b].ShouldBe("found");
        dictionary[c].ShouldBe("found");
        set.Count.ShouldBe(1);
        a.TryGetParameter("x", out string? first).ShouldBeTrue();
        first.ShouldBe("a");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Media equality: Match repeated names across bitmap and bounded-stack limits")]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(4096)]
    [InlineData(4097)]
    public void Equals_LargeRepeatedLists_ShouldPreserveNameGroupOrder(int count)
    {
        // Arrange
        string[] entries = Enumerable.Range(0, count).Select(i => $"p{i % 3}=v{i}").ToArray();
        string[] grouped = entries.OrderBy(entry => entry[1]).ToArray();
        HttpMediaType left = HttpMediaType.Parse("text/plain; " + string.Join(';', entries));
        HttpMediaType right = HttpMediaType.Parse("text/plain; " + string.Join(';', grouped));
        grouped[^1] += "different";
        HttpMediaType different = HttpMediaType.Parse("text/plain; " + string.Join(';', grouped));

        // Act / Assert
        left.Equals(right).ShouldBeTrue();
        right.Equals(left).ShouldBeTrue();
        left.Equals(different).ShouldBeFalse();
        different.Equals(left).ShouldBeFalse();
    }
}
