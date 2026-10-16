using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class ParserResourceLimitTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Parse: hostile nesting survives a child process")]
    [InlineData("quotes", 1000)]
    [InlineData("quotes", 10000)]
    [InlineData("quotes", 100000)]
    [InlineData("lists", 1000)]
    [InlineData("lists", 10000)]
    [InlineData("lists", 100000)]
    [InlineData("emphasis", 1000)]
    [InlineData("emphasis", 10000)]
    [InlineData("emphasis", 100000)]
    [InlineData("links", 1000)]
    [InlineData("links", 10000)]
    [InlineData("links", 100000)]
    [InlineData("images", 1000)]
    [InlineData("images", 10000)]
    [InlineData("images", 100000)]
    [InlineData("destinations", 1000)]
    [InlineData("destinations", 10000)]
    [InlineData("destinations", 100000)]
    public async Task Parse_DepthAndAliasBombs_CompletesInChildAsync(string shape, int depth)
    {
        // Arrange. Run the parser in an isolated dotnet process so a regression cannot kill xUnit.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var result = await ParserBombProbe.RunAsync(shape, depth, timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldStartWith("Accepted", Case.Sensitive);

        var document = MarkdownText.Parse(ParserBombProbe.CreateInput(shape, depth));
        MarkdownText.ToHtml(document).ShouldContain("end", Case.Sensitive);
    }
}
