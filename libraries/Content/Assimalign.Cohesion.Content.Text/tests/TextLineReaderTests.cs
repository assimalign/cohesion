using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Text.Tests;

public class TextLineReaderTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Text] - Lines: mixed terminators are recognized and reported")]
    public void TryReadLine_MixedTerminators_ReportsEndings()
    {
        var lines = ReadAll("one\ntwo\r\nthree\rfour");

        lines.Count.ShouldBe(4);
        lines[0].Text.ShouldBe("one");
        lines[0].Ending.ShouldBe(TextLineEnding.LineFeed);
        lines[1].Text.ShouldBe("two");
        lines[1].Ending.ShouldBe(TextLineEnding.CarriageReturnLineFeed);
        lines[2].Text.ShouldBe("three");
        lines[2].Ending.ShouldBe(TextLineEnding.CarriageReturn);
        lines[3].Text.ShouldBe("four");
        lines[3].Ending.ShouldBe(TextLineEnding.None);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Lines: line numbers are one-based and sequential")]
    public void TryReadLine_LineNumbers_AreSequential()
    {
        var lines = ReadAll("a\nb\nc");

        lines[0].Number.ShouldBe(1);
        lines[1].Number.ShouldBe(2);
        lines[2].Number.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Lines: empty lines are preserved")]
    public void TryReadLine_EmptyLines_ArePreserved()
    {
        var lines = ReadAll("a\n\nb");

        lines.Count.ShouldBe(3);
        lines[1].Text.ShouldBe(string.Empty);
        lines[1].Ending.ShouldBe(TextLineEnding.LineFeed);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Lines: empty input yields no lines")]
    public void TryReadLine_EmptyInput_YieldsNoLines()
    {
        ReadAll(string.Empty).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Lines: a trailing terminator does not create a phantom line")]
    public void TryReadLine_TrailingTerminator_NoPhantomLine()
    {
        var lines = ReadAll("a\n");

        lines.Count.ShouldBe(1);
        lines[0].Text.ShouldBe("a");
    }

    private static List<TextLine> ReadAll(string text)
    {
        using var reader = new TextLineReader(new StringReader(text));
        var lines = new List<TextLine>();
        while (reader.TryReadLine(out var line))
        {
            lines.Add(line);
        }

        return lines;
    }
}
