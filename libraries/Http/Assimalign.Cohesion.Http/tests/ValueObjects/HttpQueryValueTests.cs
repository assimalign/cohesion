using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpQueryValueTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpQueryValue: Concat preserves values and independent snapshots")]
    public void Concat_OnRepeatedValues_ShouldPreserveOrderAndSnapshots()
    {
        // Arrange
        HttpQueryValue values = "0";
        for (int index = 1; index < 10; index++)
        {
            values = HttpQueryValue.Concat(values, index.ToString());
        }

        // Act
        HttpQueryValue firstBranch = HttpQueryValue.Concat(values, "first");
        HttpQueryValue secondBranch = HttpQueryValue.Concat(values, "second");

        // Assert
        values.ToArray().ShouldBe(Enumerable.Range(0, 10).Select(index => index.ToString()).ToArray());
        firstBranch.Take(10).ShouldBe(values);
        secondBranch.Take(10).ShouldBe(values);
        firstBranch[10].ShouldBe("first");
        secondBranch[10].ShouldBe("second");
        values.Value.ShouldBe("0");
        values.GetInt32().ShouldBe(0);
        firstBranch.ShouldNotBe(secondBranch);
        firstBranch.ShouldBe(HttpQueryValue.Concat(values, "first"));
        firstBranch.GetHashCode().ShouldBe(HttpQueryValue.Concat(values, "first").GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpQueryValue: Empty differs from one empty occurrence")]
    public void Empty_OnMissingValue_ShouldHaveNoOccurrences()
    {
        // Arrange
        HttpQueryValue missing = HttpQueryValue.Empty;
        HttpQueryValue present = new(string.Empty);

        // Act
        HttpQueryValue combined = HttpQueryValue.Concat(missing, present);

        // Assert
        missing.Count.ShouldBe(0);
        missing.Value.ShouldBeEmpty();
        present.Count.ShouldBe(1);
        combined.ShouldBe(present);
        missing.ShouldNotBe(present);
        Should.Throw<IndexOutOfRangeException>(() => missing[0]);
    }
}
