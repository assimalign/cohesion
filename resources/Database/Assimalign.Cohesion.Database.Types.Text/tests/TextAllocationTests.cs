using System;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Types.Text.Internal;

namespace Assimalign.Cohesion.Database.Types.Text.Tests;

public sealed class TextAllocationTests
{
    private readonly ITestOutputHelper _output;

    public TextAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Allocation: analysis has a deterministic per-token bound, minimum of five rounds")]
    [InlineData("simple", false)]
    [InlineData("simple", true)]
    [InlineData("english", false)]
    [InlineData("english", true)]
    public void Analyze_RepeatedTokens_BoundsAllocationPerToken(string name, bool foldAccents)
    {
        // Arrange
        TextAnalyzer analyzer = TextAnalyzer.Create(TextAnalyzerDescriptor.Create(name, 1, foldAccents));
        string input = string.Concat(Enumerable.Repeat("CAFÉ running relational digit123 ", 1024));
        analyzer.Analyze(input);

        // Act
        long bytes = MinimumAllocation(() => analyzer.Analyze(input));

        // Assert
        _output.WriteLine($"{name} accents={foldAccents}: {bytes} bytes / 4096 tokens = {bytes / 4096d:F2} bytes/token ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        bytes.ShouldBeLessThanOrEqualTo(4096L * RepeatedTokenCap(name, foldAccents));
    }

    [Theory(DisplayName = "Cohesion Test [Text] - Allocation: distinct retained terms remain bounded per token at two corpus sizes")]
    [InlineData("simple")]
    [InlineData("english")]
    public void Analyze_DistinctTokens_BoundsRetainedAllocationPerToken(string name)
    {
        // Arrange
        TextAnalyzer analyzer = TextAnalyzer.Create(TextAnalyzerDescriptor.Create(name, 1));
        foreach (int count in new[] { 1024, 2048 })
        {
            string input = string.Join(" ", Enumerable.Range(0, count).Select(n => "term" + n.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)));
            analyzer.Analyze(input).Lexemes.Count.ShouldBe(count);

            // Act
            long bytes = MinimumAllocation(() => analyzer.Analyze(input));

            // Assert
            _output.WriteLine($"{name} distinct: {bytes} bytes / {count} tokens = {bytes / (double)count:F2} bytes/token");
            bytes.ShouldBeLessThanOrEqualTo(count * DistinctTokenCap);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Allocation: skipped oversized words allocate no token buffer")]
    public void Analyze_OversizedToken_UsesConstantWorkspace()
    {
        // Arrange
        string input = new('a', 1000000);
        TextAnalyzer.Simple.Analyze(input);

        // Act
        long bytes = MinimumAllocation(() => TextAnalyzer.Simple.Analyze(input));

        // Assert
        _output.WriteLine($"Oversized token: {bytes} bytes");
        bytes.ShouldBeLessThanOrEqualTo(280);
    }

    [Fact(DisplayName = "Cohesion Test [Text] - Allocation: the in-house stemmer uses bounded token workspace")]
    public void Stem_SuffixCorpus_BoundsAllocationPerToken()
    {
        // Arrange
        string[] words = ["relational", "conditional", "rational", "valenci", "hesitanci", "digitizer", "conformabli", "radicalli", "running", "generously"];
        foreach (string word in words)
        {
            Porter2Stemmer.Stem(word);
        }

        // Act
        long bytes = MinimumAllocation(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                foreach (string word in words)
                {
                    Porter2Stemmer.Stem(word);
                }
            }
        });

        // Assert
        _output.WriteLine($"Stemmer: {bytes} bytes / 10000 tokens = {bytes / 10000d:F2} bytes/token");
        bytes.ShouldBeLessThanOrEqualTo(10000L * 90);
    }

#if DEBUG
    private const int DistinctTokenCap = 910;

    private static int RepeatedTokenCap(string name, bool foldAccents) =>
        (name, foldAccents) switch
        {
            ("simple", false) => 610,
            ("english", false) => 650,
            ("simple", true) => 1240,
            _ => 1280,
        };
#else
    private const int DistinctTokenCap = 910;

    private static int RepeatedTokenCap(string name, bool foldAccents) => name == "simple" ? 250 : 290;
#endif

    private static long MinimumAllocation(Action action)
    {
        long minimum = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return minimum;
    }
}
