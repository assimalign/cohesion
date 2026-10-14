using System;
using System.Collections.Generic;
using System.Globalization;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

[Collection(GeneratedValueTypeCollection.Name)]
public sealed class GeneratedValueTypeRegressionTests
{
    private const string displayPrefix = "Cohesion Test [Core] - Generated value type regressions: ";

    [Fact(DisplayName = displayPrefix + "default strings compare equal and hash without a value")]
    public void Equality_DefaultStrings_ShouldBeEqualAndHashable()
    {
        // Arrange
        GeneratedString first = default;
        GeneratedString second = default;

        // Act
        bool equal = first == second;
        var values = new HashSet<GeneratedString> { first, second };

        // Assert
        equal.ShouldBeTrue();
        (first != second).ShouldBeFalse();
        first.Equals(second).ShouldBeTrue();
        first.Equals((object)second).ShouldBeTrue();
        first.Equals((object?)null).ShouldBeFalse();
        first.GetHashCode().ShouldBe(0);
        values.Count.ShouldBe(1);
    }

    [Fact(DisplayName = displayPrefix + "default strings sort before empty and populated values")]
    public void CompareTo_DefaultString_ShouldSortFirst()
    {
        // Arrange
        GeneratedString missing = default;
        var empty = new GeneratedString(string.Empty);
        var value = new GeneratedString("a");

        // Act & Assert
        missing.CompareTo(default).ShouldBe(0);
        (missing < empty).ShouldBeTrue();
        (missing <= value).ShouldBeTrue();
        (value > missing).ShouldBeTrue();
        (value >= missing).ShouldBeTrue();
        (missing > value).ShouldBeFalse();
        (missing >= empty).ShouldBeFalse();
        (missing == empty).ShouldBeFalse();
    }

    [Fact(DisplayName = displayPrefix + "ordinal string ordering is stable under Danish culture")]
    public void CompareTo_DanishCulture_ShouldUseOrdinalOrder()
    {
        // Arrange
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        var first = new GeneratedString("aa");
        var last = new GeneratedString("z");

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");

            // Act
            bool isOrdered = first < last;
            var sorted = new SortedSet<GeneratedString> { last, first };

            // Assert
            isOrdered.ShouldBeTrue();
            first.CompareTo(last).ShouldBeLessThan(0);
            sorted.Min.ShouldBe(first);
            (new GeneratedIgnoreCaseString("AA") < new GeneratedIgnoreCaseString("z")).ShouldBeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact(DisplayName = displayPrefix + "ignore-case strings share equality hashes and ordering")]
    public void Equality_IgnoreCaseStrings_ShouldFoldCaseConsistently()
    {
        // Arrange
        var first = new GeneratedIgnoreCaseString("Example.COM");
        var second = new GeneratedIgnoreCaseString("example.com");
        GeneratedIgnoreCaseString missing = default;

        // Act
        var values = new HashSet<GeneratedIgnoreCaseString> { first, second };

        // Assert
        (first == second).ShouldBeTrue();
        first.Equals((object)second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.CompareTo(second).ShouldBe(0);
        values.Count.ShouldBe(1);
        (missing < first).ShouldBeTrue();
        missing.GetHashCode().ShouldBe(0);
        (new GeneratedString("A") == new GeneratedString("a")).ShouldBeFalse();
    }

    [Fact(DisplayName = displayPrefix + "Parse honors an explicit provider independently of current culture")]
    public void Parse_ExplicitProvider_ShouldHonorNumberFormat()
    {
        // Arrange
        var provider = new NumberFormatInfo { NumberDecimalSeparator = ":", NumberGroupSeparator = "_" };

        // Act
        GeneratedDecimal fromString = GeneratedDecimal.Parse("1:5", provider);
        GeneratedDecimal fromSpan = GeneratedDecimal.Parse("1:5".AsSpan(), provider);

        // Assert
        fromString.Value.ShouldBe(1.5m);
        fromSpan.ShouldBe(fromString);
        GeneratedDecimal.TryParse("1:5", provider, out var tryString).ShouldBeTrue();
        GeneratedDecimal.TryParse("1:5".AsSpan(), provider, out var trySpan).ShouldBeTrue();
        tryString.ShouldBe(fromString);
        trySpan.ShouldBe(fromString);
        fromString.ToString("F1", provider).ShouldBe("1:5");
    }

    [Fact(DisplayName = displayPrefix + "TryParse does not throw when a parsed number fails validation")]
    public void TryParse_ParseableButInvalidValue_ShouldReturnFalse()
    {
        // Arrange
        const string input = "11";

        // Act
        bool parsed = GeneratedRangeInt.TryParse(input, CultureInfo.InvariantCulture, out GeneratedRangeInt result);

        // Assert
        parsed.ShouldBeFalse();
        result.ShouldBe(default);
    }

    [Fact(DisplayName = displayPrefix + "Parse reports rejected numbers as FormatException")]
    public void Parse_ParseableButInvalidValue_ShouldThrowFormatException()
    {
        // Arrange
        const string input = "11";

        // Act
        Action parse = () => GeneratedRangeInt.Parse(input, CultureInfo.InvariantCulture);

        // Assert
        Should.Throw<FormatException>(parse).Message.ShouldBe(GeneratedRangeInt.InvalidMessage);
    }

    [Fact(DisplayName = displayPrefix + "ToString returns an empty string for default")]
    public void ToString_DefaultString_ShouldReturnEmptyString()
    {
        // Arrange
        GeneratedString value = default;

        // Act
        string text = value.ToString();

        // Assert
        text.ShouldBe(string.Empty);
    }
}
