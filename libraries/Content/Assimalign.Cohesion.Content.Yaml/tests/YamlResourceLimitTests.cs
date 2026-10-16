using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlResourceLimitTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse: default depth boundary accepts 512 collections")]
    [InlineData("flow-sequence")]
    [InlineData("flow-mapping")]
    [InlineData("block-sequence")]
    [InlineData("block-mapping")]
    [InlineData("indented-sequence")]
    [InlineData("indented-mapping")]
    public void Parse_DefaultDepthBoundary_Accepts(string shape)
    {
        // Arrange
        var source = ParserBombProbe.CreateInput(shape, 512);

        // Act / Assert
        Should.NotThrow(() => YamlText.Parse(source));
        Should.Throw<YamlException>(() => YamlText.Parse(source, new YamlParserOptions { MaxDepth = 511 }))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - ParseEvents: nested keys cannot bypass the final depth budget")]
    [InlineData("[[x]: y]", 3)]
    [InlineData("[[x]]: y", 3)]
    [InlineData("? [x]\n: y", 2)]
    public void ParseEvents_RetroactiveMappingStart_CountsKeyDepth(string source, int depth)
    {
        // Arrange
        var atLimit = new YamlParserOptions { MaxDepth = depth };
        var belowLimit = new YamlParserOptions { MaxDepth = depth - 1 };

        // Act / Assert
        Should.NotThrow(() => YamlText.ParseEvents(source, atLimit));
        Should.Throw<YamlException>(() => YamlText.ParseEvents(source, belowLimit))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse: aliases charge expanded targets including collection and key nodes")]
    [InlineData("[&a [x], *a, *a]", 4)]
    [InlineData("[&a {x: y}, *a, *a]", 6)]
    [InlineData("[&a [x], &b [*a, *a], *b]", 9)]
    [InlineData("&a [*a]", 1)]
    [InlineData("[&a x, *a, &a [y], *a]", 3)]
    public void Parse_AliasBudgetBoundary_ChargesExpandedNodes(string source, long budget)
    {
        // Arrange
        var options = new YamlParserOptions { MaxAliasExpansion = budget };

        // Act / Assert
        Should.NotThrow(() => YamlText.Parse(source, options));
        Should.Throw<YamlException>(() => YamlText.ParseEvents(source, new YamlParserOptions { MaxAliasExpansion = budget - 1 }))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: alias budget resets at document boundaries and preserves cycles")]
    public void Parse_DocumentsAndCycles_PreservesIdentity()
    {
        // Arrange
        const string source = "---\n&a [*a]\n---\n&a [*a]";

        // Act
        var stream = YamlText.Parse(source, new YamlParserOptions { MaxAliasExpansion = 1 });

        // Assert
        stream.Count.ShouldBe(2);
        foreach (var document in stream.Documents)
        {
            var sequence = document.Root.ShouldBeOfType<YamlSequence>();
            sequence[0].ShouldBeSameAs(sequence);
        }
        Should.Throw<YamlException>(() => YamlText.Parse("[&a x, *a]", new YamlParserOptions { MaxAliasExpansion = 0 }))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: nine-by-nine alias bomb is rejected with bounded work")]
    public void Parse_AliasBomb_UsesBoundedTimeAndAllocation()
    {
        // Arrange. The fixture is authored by ParserBombProbe; input generation is outside measurement.
        var source = ParserBombProbe.CreateInput("aliases", 9);
        YamlText.Parse("warmup");
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();

        // Act
        var exception = Should.Throw<YamlException>(() => YamlText.Parse(source));
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

        // Assert
        exception.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        bytes.ShouldBeLessThan(2_000_000);
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: expansion arithmetic saturates at the largest caller budget")]
    public void Parse_HugeExpansionBudget_DoesNotOverflow()
    {
        // Arrange. Authored 64-level doubling DAG; its source and event graph are tiny.
        var source = new StringBuilder("a0: &a0 x\n");
        for (var index = 1; index < 64; index++)
        {
            source.Append($"a{index}: &a{index} [*a{index - 1}, *a{index - 1}]\n");
        }

        // Act / Assert
        Should.Throw<YamlException>(() => YamlText.Parse(source.ToString(), new YamlParserOptions { MaxAliasExpansion = long.MaxValue }))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - Parse: all facades and async reader enforce supplied limits")]
    public async Task Parse_AllEntryPoints_EnforceOptionsAsync()
    {
        // Arrange
        const string source = "[[x]]";
        var options = new YamlParserOptions { MaxDepth = 1 };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var content = TextContent.Create(source);
        var reader = YamlText.CreateReader(options);

        // Act / Assert
        Should.Throw<YamlException>(() => YamlText.ParseDocument(source, options)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        Should.Throw<YamlException>(() => YamlText.ParseEvents(source, options)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        Should.Throw<YamlException>(() => YamlText.Parse(content, options)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        Should.Throw<YamlException>(() => YamlText.Parse(stream, options)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        stream.Position = 0;
        Should.Throw<YamlException>(() => reader.Read(stream)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        stream.Position = 0;
        var exception = await Should.ThrowAsync<YamlException>(async () => await reader.ReadAsync(stream, CancellationToken.None));
        exception.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse: invalid options are rejected at composition")]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void Parse_InvalidOptions_ThrowsArgumentException(int depth, long aliases)
    {
        // Arrange
        var options = new YamlParserOptions { MaxDepth = depth, MaxAliasExpansion = aliases };

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => YamlText.CreateReader(options));
        Should.Throw<ArgumentOutOfRangeException>(() => YamlText.Parse("", options));
        Should.Throw<ArgumentOutOfRangeException>(() => YamlText.ParseEvents("", options));
    }
}
