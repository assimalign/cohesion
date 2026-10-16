using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class ParserResourceLimitTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse: hostile nesting survives a child process")]
    [InlineData("flow-sequence", 1000)]
    [InlineData("flow-sequence", 10000)]
    [InlineData("flow-sequence", 100000)]
    [InlineData("flow-mapping", 1000)]
    [InlineData("flow-mapping", 10000)]
    [InlineData("flow-mapping", 100000)]
    [InlineData("block-mapping", 1000)]
    [InlineData("block-mapping", 10000)]
    [InlineData("block-mapping", 100000)]
    [InlineData("block-sequence", 1000)]
    [InlineData("block-sequence", 10000)]
    [InlineData("block-sequence", 100000)]
    [InlineData("aliases", 9)]
    [InlineData("raised-depth", 100000)]
    [InlineData("properties", 100000)]
    [InlineData("indented-mapping", 1000)]
    [InlineData("indented-sequence", 1000)]
    public async Task Parse_DepthAndAliasBombs_CompletesInChildAsync(string shape, int depth)
    {
        // Arrange. Run the parser in an isolated dotnet process so a regression cannot kill xUnit.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var result = await ParserBombProbe.RunAsync(shape, depth, timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
        result.Output.ShouldStartWith("LimitExceeded", Case.Sensitive);

        // Re-run only after isolation proves the guard works, then assert the in-process contract.
        var exception = Should.Throw<YamlException>(() => YamlText.Parse(ParserBombProbe.CreateInput(shape, depth)));
        exception.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        exception.Position.ShouldNotBeNull();
        exception.Line.ShouldBeGreaterThan(0);
        exception.Column.ShouldBeGreaterThan(0);
    }
}
