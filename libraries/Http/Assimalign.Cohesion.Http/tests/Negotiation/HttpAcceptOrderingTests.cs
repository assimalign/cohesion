using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpAcceptOrderingTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - ParseAccept: Preserve stable preference order across list sizes")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(64)]
    public void ParseAccept_MixedRanks_ShouldPreserveStablePreferenceOrder(int count)
    {
        // Arrange
        var entries = new HttpMediaTypeQuality[count];
        var segments = new string[count];
        for (int i = 0; i < count; i++)
        {
            string media = (i % 5) switch
            {
                0 => "*/*",
                1 => "text/*",
                2 => $"text/type{i}; note=\"a,b;c\"",
                _ => $"application/type{i}",
            };
            int weight = i / 2 % 10;
            entries[i] = new HttpMediaTypeQuality(HttpMediaType.Parse(media), HttpQuality.FromPerMille(weight * 100));
            segments[i] = $"{media};q=0.{weight.ToString(CultureInfo.InvariantCulture)};ext=ignored";
        }
        string raw = "bad/, application/bad;q=invalid,," + string.Join(',', segments);
        HttpMediaTypeQuality[] expected = entries.OrderByDescending(entry => entry.Quality.PerMille)
            .ThenByDescending(entry => entry.MediaType.Specificity).ToArray();

        // Act
        IReadOnlyList<HttpMediaTypeQuality> actual = HttpAcceptParser.ParseAccept(raw);

        // Assert
        actual.ToArray().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Parse token lists: Preserve stable quality and wildcard order")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(64)]
    public void ParseTokenLists_MixedRanks_ShouldPreserveStablePreferenceOrder(int count)
    {
        // Arrange
        var entries = new HttpQualityValue[count];
        var segments = new string[count];
        for (int i = 0; i < count; i++)
        {
            string token = i % 5 == 0 ? "*" : $"Token{i}";
            int weight = i / 2 % 10;
            entries[i] = new HttpQualityValue(token, HttpQuality.FromPerMille(weight * 100));
            segments[i] = $"{token};q=0.{weight.ToString(CultureInfo.InvariantCulture)}";
        }
        string raw = "bad/token,invalid;q=invalid,," + string.Join(',', segments);
        HttpQualityValue[] expected = entries.OrderByDescending(entry => entry.Quality.PerMille)
            .ThenBy(entry => entry.IsWildcard).ToArray();

        // Act
        IReadOnlyList<HttpQualityValue> encodings = HttpAcceptParser.ParseAcceptEncoding(raw);
        IReadOnlyList<HttpQualityValue> languages = HttpAcceptParser.ParseAcceptLanguage(raw);
        IReadOnlyList<HttpQualityValue> charsets = HttpAcceptParser.ParseAcceptCharset(raw);

        // Assert
        encodings.ToArray().ShouldBe(expected);
        languages.ToArray().ShouldBe(expected);
        charsets.ToArray().ShouldBe(expected);
    }
}
