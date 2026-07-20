using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Text.Tests;

public class TextTokenizerTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: default options produce text and new-line tokens")]
    public void TryRead_DefaultOptions_ProducesTextAndNewLineTokens()
    {
        var tokens = ReadAll("one\ntwo\r\nthree\rfour");

        tokens.Count.ShouldBe(7);
        tokens[0].Kind.ShouldBe(TextTokenKind.Text);
        tokens[0].ToString().ShouldBe("one");
        tokens[1].Kind.ShouldBe(TextTokenKind.NewLine);
        tokens[1].Definition.ShouldBeSameAs(TextTokenDefinition.LineFeed);
        tokens[2].ToString().ShouldBe("two");
        tokens[3].Definition.ShouldBeSameAs(TextTokenDefinition.CarriageReturnLineFeed);
        tokens[3].ToString().ShouldBe("\r\n");
        tokens[4].ToString().ShouldBe("three");
        tokens[5].Definition.ShouldBeSameAs(TextTokenDefinition.CarriageReturn);
        tokens[5].ToString().ShouldBe("\r");
        tokens[6].Kind.ShouldBe(TextTokenKind.Text);
        tokens[6].ToString().ShouldBe("four");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: empty input yields no tokens")]
    public void TryRead_EmptyInput_YieldsNoTokens()
    {
        ReadAll(string.Empty).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: token-free input is one text run starting at the start position")]
    public void TryRead_TextOnly_YieldsSingleTextToken()
    {
        var tokens = ReadAll("plain text");

        tokens.Count.ShouldBe(1);
        tokens[0].Kind.ShouldBe(TextTokenKind.Text);
        tokens[0].ToString().ShouldBe("plain text");
        tokens[0].Position.ShouldBe(TextPosition.Start);
        tokens[0].Definition.ShouldBeNull();
        tokens[0].Id.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: a trailing terminator produces no phantom text token")]
    public void TryRead_TrailingTerminator_NoPhantomText()
    {
        var tokens = ReadAll("a\n");

        tokens.Count.ShouldBe(2);
        tokens[0].ToString().ShouldBe("a");
        tokens[1].Kind.ShouldBe(TextTokenKind.NewLine);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: custom delimiters match and carry their assigned ids")]
    public void TryRead_CustomDelimiters_MatchAndCarryIds()
    {
        var options = new TextTokenizerOptions();
        var hash = new TextTokenDefinition("#", id: 1);
        var star = new TextTokenDefinition("*", id: 2);
        options.Tokens.Add(hash);
        options.Tokens.Add(star);

        var tokens = ReadAll("# Title *x*", options);

        tokens.Count.ShouldBe(5);
        tokens[0].Kind.ShouldBe(TextTokenKind.Delimiter);
        tokens[0].Definition.ShouldBeSameAs(hash);
        tokens[0].Id.ShouldBe(1);
        tokens[1].ToString().ShouldBe(" Title ");
        tokens[2].Definition.ShouldBeSameAs(star);
        tokens[3].ToString().ShouldBe("x");
        tokens[4].Id.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: the longest literal sharing a first character wins")]
    public void TryRead_SharedFirstCharacter_LongestLiteralWins()
    {
        var options = new TextTokenizerOptions();
        var star = new TextTokenDefinition("*", id: 1);
        var doubleStar = new TextTokenDefinition("**", id: 2);
        options.Tokens.Add(star);
        options.Tokens.Add(doubleStar);

        var tokens = ReadAll("**a*", options);

        tokens.Count.ShouldBe(3);
        tokens[0].Definition.ShouldBeSameAs(doubleStar);
        tokens[1].ToString().ShouldBe("a");
        tokens[2].Definition.ShouldBeSameAs(star);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: an unmatched candidate prefix flows into the text run")]
    public void TryRead_UnmatchedCandidatePrefix_FlowsIntoText()
    {
        var options = new TextTokenizerOptions();
        var doubleStar = new TextTokenDefinition("**");
        options.Tokens.Add(doubleStar);

        var tokens = ReadAll("*a**b", options);

        tokens.Count.ShouldBe(3);
        tokens[0].Kind.ShouldBe(TextTokenKind.Text);
        tokens[0].ToString().ShouldBe("*a");
        tokens[1].Definition.ShouldBeSameAs(doubleStar);
        tokens[2].ToString().ShouldBe("b");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: overridden defaults let unlisted terminators flow into text")]
    public void TryRead_OverriddenDefaults_UnlistedTerminatorsFlowIntoText()
    {
        var options = new TextTokenizerOptions();
        options.Tokens.Clear();
        options.Tokens.Add(TextTokenDefinition.LineFeed);

        var tokens = ReadAll("a\r\nb", options);

        tokens.Count.ShouldBe(3);
        tokens[0].ToString().ShouldBe("a\r");
        tokens[1].Definition.ShouldBeSameAs(TextTokenDefinition.LineFeed);
        tokens[1].Position.ShouldBe(new TextPosition(2, 1, 2));
        tokens[2].ToString().ShouldBe("b");
        tokens[2].Position.ShouldBe(new TextPosition(2, 1, 3));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: a cleared table produces one text run while lines keep counting")]
    public void TryRead_ClearedTokens_WholeInputIsOneTextRun()
    {
        var options = new TextTokenizerOptions();
        options.Tokens.Clear();
        var tokenizer = new TextTokenizer("a\nb", options);

        tokenizer.TryRead(out var token).ShouldBeTrue();
        token.Kind.ShouldBe(TextTokenKind.Text);
        token.ToString().ShouldBe("a\nb");
        tokenizer.TryRead(out _).ShouldBeFalse();
        tokenizer.Position.ShouldBe(new TextPosition(2, 2, 3));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: whitespace runs are emitted when enabled")]
    public void TryRead_TokenizeWhitespace_EmitsWhitespaceRuns()
    {
        var options = new TextTokenizerOptions { TokenizeWhitespace = true };

        var tokens = ReadAll("one  \ttwo three", options);

        tokens.Count.ShouldBe(5);
        tokens[0].ToString().ShouldBe("one");
        tokens[1].Kind.ShouldBe(TextTokenKind.Whitespace);
        tokens[1].ToString().ShouldBe("  \t");
        tokens[1].Definition.ShouldBeNull();
        tokens[2].ToString().ShouldBe("two");
        tokens[3].ToString().ShouldBe(" ");
        tokens[4].ToString().ShouldBe("three");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: a literal starting with whitespace is recognized inside a run")]
    public void TryRead_WhitespaceOverlappingDefinition_StopsRunAtLiteral()
    {
        var options = new TextTokenizerOptions { TokenizeWhitespace = true };
        var dash = new TextTokenDefinition("\t-", id: 9);
        options.Tokens.Add(dash);

        var tokens = ReadAll("a \t- b", options);

        tokens.Count.ShouldBe(5);
        tokens[0].ToString().ShouldBe("a");
        tokens[1].Kind.ShouldBe(TextTokenKind.Whitespace);
        tokens[1].ToString().ShouldBe(" ");
        tokens[2].Definition.ShouldBeSameAs(dash);
        tokens[3].Kind.ShouldBe(TextTokenKind.Whitespace);
        tokens[3].ToString().ShouldBe(" ");
        tokens[4].ToString().ShouldBe("b");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: positions report one-based lines and columns with char offsets")]
    public void TryRead_Positions_TrackLinesColumnsAndOffsets()
    {
        var options = new TextTokenizerOptions();
        options.Tokens.Add(new TextTokenDefinition("#"));

        var tokens = ReadAll("ab\n#cd", options);

        tokens.Count.ShouldBe(4);
        tokens[0].Position.ShouldBe(new TextPosition(1, 1, 0));
        tokens[1].Position.ShouldBe(new TextPosition(1, 3, 2));
        tokens[2].Position.ShouldBe(new TextPosition(2, 1, 3));
        tokens[3].Position.ShouldBe(new TextPosition(2, 2, 4));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: literals match across segment boundaries")]
    public void TryRead_MultiSegmentSequence_MatchesLiteralAcrossSegments()
    {
        var options = new TextTokenizerOptions();
        var doubleStar = new TextTokenDefinition("**");
        options.Tokens.Add(doubleStar);
        var sequence = TestSequenceFactory.Create("ab*", "*cd");

        var tokens = ReadAll(sequence, options);

        tokens.Count.ShouldBe(3);
        tokens[0].ToString().ShouldBe("ab");
        tokens[1].Definition.ShouldBeSameAs(doubleStar);
        tokens[1].ToString().ShouldBe("**");
        tokens[2].ToString().ShouldBe("cd");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: a terminator split across segments counts one line break")]
    public void TryRead_MultiSegmentSequence_TracksPositionsAcrossSegments()
    {
        var sequence = TestSequenceFactory.Create("a\r", "\nb");

        var tokens = ReadAll(sequence, options: null);

        tokens.Count.ShouldBe(3);
        tokens[0].ToString().ShouldBe("a");
        tokens[1].Definition.ShouldBeSameAs(TextTokenDefinition.CarriageReturnLineFeed);
        tokens[1].Position.ShouldBe(new TextPosition(1, 2, 1));
        tokens[2].ToString().ShouldBe("b");
        tokens[2].Position.ShouldBe(new TextPosition(2, 1, 3));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: token values slice the input rather than copying")]
    public void TryRead_TokenValues_SliceTheInput()
    {
        var memory = "x*y".AsMemory();
        var options = new TextTokenizerOptions();
        options.Tokens.Add(new TextTokenDefinition("*"));
        var tokenizer = new TextTokenizer(memory, options);

        tokenizer.TryRead(out var token).ShouldBeTrue();

        token.Value.IsSingleSegment.ShouldBeTrue();
        var overlaps = MemoryMarshal.TryGetString(token.Value.First, out var text, out var start, out _);
        overlaps.ShouldBeTrue();
        text.ShouldBe("x*y");
        start.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: duplicate definition texts are rejected")]
    public void Constructor_DuplicateDefinitions_Throws()
    {
        var options = new TextTokenizerOptions();
        options.Tokens.Add(new TextTokenDefinition("*"));
        options.Tokens.Add(new TextTokenDefinition("*", id: 2));

        Should.Throw<ArgumentException>(() => new TextTokenizer("a", options));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: a null definition entry is rejected")]
    public void Constructor_NullDefinitionEntry_Throws()
    {
        var options = new TextTokenizerOptions();
        options.Tokens.Add(null!);

        Should.Throw<ArgumentException>(() => new TextTokenizer("a", options));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: tokenizing a null string is rejected")]
    public void Constructor_NullString_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new TextTokenizer((string)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: definitions reject empty text and the implicit text kind")]
    public void Definition_InvalidArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => new TextTokenDefinition(null!));
        Should.Throw<ArgumentException>(() => new TextTokenDefinition(string.Empty));
        Should.Throw<ArgumentException>(() => new TextTokenDefinition("x", kind: TextTokenKind.Text));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: reads after the end keep returning false")]
    public void TryRead_AfterEnd_ReturnsFalse()
    {
        var tokenizer = new TextTokenizer("a");

        tokenizer.TryRead(out _).ShouldBeTrue();
        tokenizer.TryRead(out _).ShouldBeFalse();
        tokenizer.TryRead(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Text] - Tokenizer: a default instance reads nothing")]
    public void TryRead_DefaultInstance_ReturnsFalse()
    {
        var tokenizer = default(TextTokenizer);

        tokenizer.TryRead(out _).ShouldBeFalse();
    }

    private static List<TextToken> ReadAll(string text, TextTokenizerOptions? options = null)
    {
        var tokenizer = new TextTokenizer(text, options);
        return ReadAll(ref tokenizer);
    }

    private static List<TextToken> ReadAll(ReadOnlySequence<char> text, TextTokenizerOptions? options)
    {
        var tokenizer = new TextTokenizer(text, options);
        return ReadAll(ref tokenizer);
    }

    private static List<TextToken> ReadAll(ref TextTokenizer tokenizer)
    {
        var tokens = new List<TextToken>();
        while (tokenizer.TryRead(out var token))
        {
            tokens.Add(token);
        }

        return tokens;
    }
}
