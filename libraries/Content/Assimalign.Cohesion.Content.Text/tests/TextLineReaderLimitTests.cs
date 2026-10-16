using System;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Text.Tests;

public class TextLineReaderLimitTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Text] - TryReadLine: exact line limit excludes all terminators")]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void TryReadLine_ExactLimit_Accepts(string ending)
    {
        // Arrange
        using var reader = new TextLineReader(new StringReader("abc" + ending), new TextLineReaderOptions { MaxLineLength = 3 });

        // Act / Assert
        reader.TryReadLine(out var line).ShouldBeTrue();
        line.Text.ShouldBe("abc");
        line.Number.ShouldBe(1);
        reader.TryReadLine(out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - TryReadLine: error position counts original UTF-16 characters and terminators")]
    [InlineData("x\nabcd", 5)]
    [InlineData("x\rabcd", 5)]
    [InlineData("x\r\nabcd", 6)]
    [InlineData("é😀\r\nabcd", 8)]
    public void TryReadLine_ExcessCharacter_ReportsAbsolutePosition(string source, long position)
    {
        // Arrange
        using var reader = new TextLineReader(new StringReader(source), new TextLineReaderOptions { MaxLineLength = 3 });
        reader.TryReadLine(out _).ShouldBeTrue();

        // Act
        var exception = Should.Throw<ContentFormatException>(() => reader.TryReadLine(out _));

        // Assert
        exception.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        exception.Position.ShouldBe(position);
        reader.LineNumber.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - TryReadLine: endless line stops at the default budget with bounded allocation")]
    public void TryReadLine_EndlessInput_BoundsWorkAndAllocation()
    {
        // Arrange
        using var source = new EndlessTextReader();
        using var reader = new TextLineReader(source);
        var allocated = GC.GetAllocatedBytesForCurrentThread();

        // Act
        var exception = Should.Throw<ContentFormatException>(() => reader.TryReadLine(out _));
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;

        // Assert
        exception.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        exception.Position.ShouldBe(1_048_576);
        source.CharactersRead.ShouldBe(1_048_577);
        bytes.ShouldBeLessThan(8_000_000);
        Should.Throw<ContentFormatException>(() => reader.TryReadLine(out _)).Position.ShouldBe(exception.Position);
        source.CharactersRead.ShouldBe(1_048_577);
        reader.Dispose();
        source.IsDisposed.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => reader.TryReadLine(out _));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - TryReadLine: caller can raise the line budget")]
    public void TryReadLine_RaisedLimit_AcceptsLongerLine()
    {
        // Arrange
        var text = new string('x', 1_048_577);
        using var reader = new TextLineReader(new StringReader(text), new TextLineReaderOptions { MaxLineLength = text.Length });

        // Act / Assert
        reader.TryReadLine(out var line).ShouldBeTrue();
        line.Text.Length.ShouldBe(text.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Text] - TextLineReader: invalid options leave the source untouched")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidOptions_DoesNotReadOrOwnSource(int maximum)
    {
        // Arrange
        using var source = new EndlessTextReader();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new TextLineReader(source, new TextLineReaderOptions { MaxLineLength = maximum }));
        source.CharactersRead.ShouldBe(0);
        source.IsDisposed.ShouldBeFalse();
    }
}
