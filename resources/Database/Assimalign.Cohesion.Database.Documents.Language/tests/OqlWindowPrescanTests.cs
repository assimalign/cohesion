using System;
using System.Diagnostics;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

[Collection("OqlWindowCompatibility")]
public sealed class OqlWindowPrescanTests
{
    private readonly ITestOutputHelper _output;

    public OqlWindowPrescanTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [OQL] - Null modifier pre-scan stays linear beyond the parser depth limit (#1805)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_DeepNullModifiers_ShouldScaleLinearly(bool balanced)
    {
        // Arrange: both review inputs reach the pre-scan before the syntax/depth refusal.
        const int count = 4096;
        var parser = new OqlQueryParser(true);
        string small = Input(count), large = Input(count * 4);
        for (int round = 0; round < 3; round++) { parser.Parse(large); parser.Parse(small); }
        // Act: compare minimums in this process; durations are otherwise diagnostics only.
        long smallTicks = Measure(small), largeTicks = Measure(large);
        // Assert: four times the tokens permits eight times the work, not quadratic growth.
        _output.WriteLine($"Balanced={balanced}; n={count}; ticks={smallTicks}; 4n ticks={largeTicks}; ratio={(double)largeTicks / smallTicks:F2}");
        largeTicks.ShouldBeLessThan(smallTicks * 8);

        string Input(int size) => "SELECT " + (balanced ? new string('(', size) + "1" : "") +
            string.Concat(Enumerable.Repeat(") respect nulls ", size));

        long Measure(string text)
        {
            long best = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long start = Stopwatch.GetTimestamp();
                var result = parser.Parse(text);
                long elapsed = Stopwatch.GetTimestamp() - start;
                result.Diagnostics.ShouldNotBeEmpty();
                best = Math.Min(best, elapsed);
            }
            return best;
        }
    }
}
